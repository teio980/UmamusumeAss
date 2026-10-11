#include "TemplateMatcher.hpp"

#include <Windows.h>
#include <bcrypt.h>
#include <catch2/catch_all.hpp>
#include <nlohmann/json.hpp>
#include <zlib.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <list>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <vector>

using UmaAssistant::Vision::GrayImage;
using UmaAssistant::Vision::MatchResult;
using UmaAssistant::Vision::Roi;
using UmaAssistant::Vision::ScaledOptions;

namespace {

struct GoldenImage
{
    GrayImage image;
    std::string sha256;
};

[[nodiscard]] std::uint32_t read_u32(std::vector<std::uint8_t> const& data, std::size_t offset)
{
    if (offset > data.size() || data.size() - offset < 4)
        throw std::runtime_error("truncated UIMG header");
    return static_cast<std::uint32_t>(data[offset])
        | (static_cast<std::uint32_t>(data[offset + 1]) << 8)
        | (static_cast<std::uint32_t>(data[offset + 2]) << 16)
        | (static_cast<std::uint32_t>(data[offset + 3]) << 24);
}

[[nodiscard]] std::string sha256(std::vector<std::uint8_t> const& bytes)
{
    BCRYPT_ALG_HANDLE algorithm{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0)
        throw std::runtime_error("BCryptOpenAlgorithmProvider(SHA256) failed");
    struct AlgorithmGuard
    {
        BCRYPT_ALG_HANDLE value;
        ~AlgorithmGuard() { BCryptCloseAlgorithmProvider(value, 0); }
    } algorithm_guard{algorithm};

    DWORD object_size = 0;
    DWORD result_size = 0;
    if (BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH,
        reinterpret_cast<PUCHAR>(&object_size), sizeof(object_size), &result_size, 0) < 0)
        throw std::runtime_error("BCryptGetProperty(BCRYPT_OBJECT_LENGTH) failed");
    std::vector<UCHAR> object(object_size);
    BCRYPT_HASH_HANDLE hash{};
    if (BCryptCreateHash(algorithm, &hash, object.data(), object_size, nullptr, 0, 0) < 0)
        throw std::runtime_error("BCryptCreateHash failed");
    struct HashGuard
    {
        BCRYPT_HASH_HANDLE value;
        ~HashGuard() { BCryptDestroyHash(value); }
    } hash_guard{hash};

    if (bytes.size() > static_cast<std::size_t>((std::numeric_limits<ULONG>::max)()))
        throw std::runtime_error("SHA256 input exceeds BCrypt limit");
    if (!bytes.empty() && BCryptHashData(hash, const_cast<PUCHAR>(bytes.data()),
        static_cast<ULONG>(bytes.size()), 0) < 0)
        throw std::runtime_error("BCryptHashData failed");
    std::array<UCHAR, 32> digest{};
    if (BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) < 0)
        throw std::runtime_error("BCryptFinishHash failed");
    constexpr char digits[] = "0123456789ABCDEF";
    std::string result;
    result.reserve(64);
    for (auto byte : digest)
    {
        result.push_back(digits[byte >> 4]);
        result.push_back(digits[byte & 0x0f]);
    }
    return result;
}

[[nodiscard]] std::vector<std::uint8_t> read_gzip(std::filesystem::path const& path)
{
    auto const narrow = path.string();
    auto* file = gzopen(narrow.c_str(), "rb");
    if (file == nullptr)
        throw std::runtime_error("cannot open gzip input: " + narrow);
    struct GzipGuard
    {
        gzFile value;
        ~GzipGuard() { gzclose(value); }
    } guard{file};

    std::vector<std::uint8_t> bytes;
    std::array<std::uint8_t, 65536> buffer{};
    while (true)
    {
        auto const count = gzread(file, buffer.data(), static_cast<unsigned>(buffer.size()));
        if (count < 0)
        {
            int error_code = Z_OK;
            auto const* error = gzerror(file, &error_code);
            throw std::runtime_error(std::string("gzip decode failed: ") + (error == nullptr ? "unknown" : error));
        }
        if (count == 0) break;
        auto const size = static_cast<std::size_t>(count);
        bytes.insert(bytes.end(), buffer.begin(), buffer.begin() + static_cast<std::ptrdiff_t>(size));
    }
    return bytes;
}

[[nodiscard]] GoldenImage load_uimg(std::filesystem::path const& path)
{
    auto const bytes = read_gzip(path);
    if (bytes.size() < 24 || bytes[0] != 'U' || bytes[1] != 'I' || bytes[2] != 'M' || bytes[3] != 'G'
        || bytes[4] != 1)
        throw std::runtime_error("invalid UIMG magic/version");
    auto const width = static_cast<std::int32_t>(read_u32(bytes, 8));
    auto const height = static_cast<std::int32_t>(read_u32(bytes, 12));
    auto const gray_length = static_cast<std::int32_t>(read_u32(bytes, 16));
    auto const rgba_length = static_cast<std::int32_t>(read_u32(bytes, 20));
    if (width <= 0 || height <= 0 || gray_length < 0 || rgba_length < -1)
        throw std::runtime_error("invalid UIMG dimensions or pixel lengths");
    auto const pixel_count = static_cast<std::uint64_t>(width) * static_cast<std::uint64_t>(height);
    if ((gray_length != 0 && static_cast<std::uint64_t>(gray_length) != pixel_count)
        || (rgba_length != -1 && static_cast<std::uint64_t>(rgba_length) != pixel_count * 4))
        throw std::runtime_error("UIMG pixel lengths do not match dimensions");
    auto const expected_size = static_cast<std::uint64_t>(24)
        + static_cast<std::uint64_t>(gray_length)
        + static_cast<std::uint64_t>((std::max)(0, rgba_length));
    if (expected_size != bytes.size())
        throw std::runtime_error("UIMG has truncated pixels or trailing bytes");

    std::vector<std::uint8_t> hash_bytes;
    hash_bytes.reserve(8 + 4 + static_cast<std::size_t>(gray_length) + 4
        + static_cast<std::size_t>((std::max)(0, rgba_length)));
    hash_bytes.insert(hash_bytes.end(), bytes.begin() + 8, bytes.begin() + 16);
    hash_bytes.insert(hash_bytes.end(), bytes.begin() + 16, bytes.begin() + 20);
    hash_bytes.insert(hash_bytes.end(), bytes.begin() + 24,
        bytes.begin() + static_cast<std::ptrdiff_t>(24 + gray_length));
    hash_bytes.insert(hash_bytes.end(), bytes.begin() + 20, bytes.begin() + 24);
    if (rgba_length > 0)
        hash_bytes.insert(hash_bytes.end(), bytes.begin() + static_cast<std::ptrdiff_t>(24 + gray_length), bytes.end());

    GrayImage image;
    image.width = width;
    image.height = height;
    image.gray.assign(bytes.begin() + 24,
        bytes.begin() + static_cast<std::ptrdiff_t>(24 + gray_length));
    if (rgba_length > 0)
        image.rgba.assign(bytes.begin() + static_cast<std::ptrdiff_t>(24 + gray_length), bytes.end());
    return {std::move(image), sha256(hash_bytes)};
}

[[nodiscard]] std::filesystem::path repo_path(std::string const& relative)
{
    auto normalized = relative;
    std::replace(normalized.begin(), normalized.end(), '/', '\\');
    return std::filesystem::path(CMAKE_SOURCE_DIR) / normalized;
}

[[nodiscard]] Roi roi_from_json(nlohmann::json const& record)
{
    auto const value = record.value("roi", nlohmann::json{});
    if (value.is_null()) return std::nullopt;
    if (!value.is_array() || value.size() != 4) throw std::runtime_error("invalid ROI in trace");
    return std::array<std::int32_t, 4>{value[0].get<std::int32_t>(), value[1].get<std::int32_t>(),
        value[2].get<std::int32_t>(), value[3].get<std::int32_t>()};
}

[[nodiscard]] std::vector<double> parse_scales(std::string_view parameters)
{
    constexpr std::string_view key = "scales=";
    auto const at = parameters.find(key);
    if (at == std::string_view::npos) throw std::runtime_error("scaled trace has no scales parameter");
    auto const begin = at + key.size();
    auto const end = parameters.find(';', begin);
    auto const field = parameters.substr(begin, end == std::string_view::npos ? end : end - begin);
    std::vector<double> values;
    std::size_t current = 0;
    while (current <= field.size())
    {
        auto const comma = field.find(',', current);
        auto const token = field.substr(current, comma == std::string_view::npos ? comma : comma - current);
        if (token.empty()) throw std::runtime_error("empty scale candidate");
        values.push_back(std::stod(std::string(token)));
        if (comma == std::string_view::npos) break;
        current = comma + 1;
    }
    return values;
}

[[nodiscard]] std::int32_t parse_int_parameter(std::string_view parameters, std::string_view key, std::int32_t fallback)
{
    auto const at = parameters.find(key);
    if (at == std::string_view::npos) return fallback;
    auto const begin = at + key.size();
    auto const end = parameters.find_first_of(";x", begin);
    return static_cast<std::int32_t>(std::stoi(std::string(parameters.substr(begin,
        end == std::string_view::npos ? end : end - begin))));
}

[[nodiscard]] MatchResult replay_call(nlohmann::json const& record, GoldenImage const& screen,
    GoldenImage const& templ)
{
    auto const roi = roi_from_json(record);
    auto const threshold = record.at("threshold").get<double>();
    auto const reference_width = record.at("referenceWidth").get<std::int32_t>();
    auto const reference_height = record.at("referenceHeight").get<std::int32_t>();
    auto const method = record.at("method").get<std::string>();
    auto const parameters = record.value("parameters", std::string{});
    if (method == "Find")
    {
        auto const pos = parameters.find("candidateStepOverride=");
        auto const is_default = pos == std::string::npos
            || parameters.compare(pos, std::string_view("candidateStepOverride=default").size(),
                "candidateStepOverride=default") == 0;
        auto const override = is_default ? std::nullopt
            : std::optional<std::int32_t>(parse_int_parameter(parameters, "candidateStepOverride=", 1));
        return UmaAssistant::Vision::find(screen.image, templ.image, roi, threshold,
            reference_width, reference_height, override);
    }
    if (method == "FindColor")
        return UmaAssistant::Vision::find_color(screen.image, templ.image, roi, threshold,
            reference_width, reference_height, parameters.find("requireTextContrast=True") != std::string::npos);
    if (method == "FindButton")
        return UmaAssistant::Vision::find_button(screen.image, templ.image, roi, threshold,
            reference_width, reference_height);
    if (method == "FindScaled")
    {
        ScaledOptions options;
        options.scales = parse_scales(parameters);
        options.candidate_step = parse_int_parameter(parameters, "candidateStep=", 8);
        auto const sample_at = parameters.find("sample=");
        if (sample_at != std::string::npos)
        {
            auto const sample = std::string_view(parameters).substr(sample_at + 7);
            auto const separator = sample.find('x');
            if (separator == std::string_view::npos) throw std::runtime_error("invalid sample parameter");
            options.sample_width = std::stoi(std::string(sample.substr(0, separator)));
            options.sample_height = std::stoi(std::string(sample.substr(separator + 1)));
        }
        options.full_search_on_miss = parameters.find("fullSearchOnMiss=False") == std::string::npos;
        return UmaAssistant::Vision::find_scaled(screen.image, templ.image, roi, threshold,
            reference_width, reference_height, options);
    }
    if (method == "FindScaledMasked")
        return UmaAssistant::Vision::find_scaled_masked(screen.image, templ.image, roi, threshold,
            reference_width, reference_height, parse_scales(parameters));
    throw std::runtime_error("unsupported matcher in golden trace: " + method);
}

class ImageCache
{
public:
    [[nodiscard]] GoldenImage const& get(std::filesystem::path const& path)
    {
        auto const key = path.string();
        auto const found = values_.find(key);
        if (found != values_.end())
        {
            order_.splice(order_.begin(), order_, found->second.order);
            return *found->second.image;
        }
        auto image = std::make_shared<GoldenImage>(load_uimg(path));
        order_.push_front(key);
        values_.emplace(key, Entry{std::move(image), order_.begin()});
        if (values_.size() > 12)
        {
            values_.erase(order_.back());
            order_.pop_back();
        }
        return *values_.at(key).image;
    }
private:
    struct Entry { std::shared_ptr<GoldenImage> image; std::list<std::string>::iterator order; };
    std::list<std::string> order_;
    std::unordered_map<std::string, Entry> values_;
};

[[nodiscard]] int replay_corpus(std::filesystem::path const& trace_path, std::size_t limit = 0)
{
    std::ifstream trace(trace_path);
    if (!trace) throw std::runtime_error("cannot open matcher trace: " + trace_path.string());
    ImageCache cache;
    std::size_t records = 0, failures = 0;
    std::string line;
    while (std::getline(trace, line))
    {
        auto const record = nlohmann::json::parse(line);
        if (record.value("recordType", std::string{}) != "match") continue;
        auto const screen_path = repo_path(record.at("screenInput").get<std::string>());
        auto const template_path = repo_path(record.at("templateInput").get<std::string>());
        auto const& screen = cache.get(screen_path);
        auto const& templ = cache.get(template_path);
        auto const& expected_screen_hash = record.at("screenSha256").get_ref<std::string const&>();
        auto const& expected_template_hash = record.at("templateSha256").get_ref<std::string const&>();
        auto const actual = replay_call(record, screen, templ);
        auto const expected_score = record.at("score").get<double>();
        auto const expected_x = record.at("x").get<std::int32_t>();
        auto const expected_y = record.at("y").get<std::int32_t>();
        auto const expected_width = record.at("width").get<std::int32_t>();
        auto const expected_height = record.at("height").get<std::int32_t>();
        auto const found_expected = record.at("found").get<bool>();
        bool ok = screen.sha256 == expected_screen_hash && templ.sha256 == expected_template_hash
            && actual.found == found_expected && std::abs(actual.score - expected_score) <= 1.0e-6
            && std::abs(actual.x - expected_x) <= 1 && std::abs(actual.y - expected_y) <= 1
            && std::abs(actual.width - expected_width) <= 1 && std::abs(actual.height - expected_height) <= 1;
        ++records;
        if (!ok)
        {
            ++failures;
            if (failures <= 20)
            {
                std::cerr << "Mismatch " << record.at("test").get<std::string>() << " / "
                    << record.at("method").get<std::string>() << ": expected " << expected_score << " @"
                    << expected_x << ',' << expected_y << " found=" << found_expected << ", got "
                    << actual.score << " @" << actual.x << ',' << actual.y << " found=" << actual.found
                    << " hashes=" << (screen.sha256 == expected_screen_hash) << ','
                    << (templ.sha256 == expected_template_hash) << '\n';
                if (record.at("method") == "Find" && found_expected
                    && expected_x >= 0 && expected_y >= 0
                    && expected_x + templ.image.width <= screen.image.width
                    && expected_y + templ.image.height <= screen.image.height)
                {
                    std::size_t equal_pixels = 0;
                    for (int y = 0; y < templ.image.height; ++y)
                        for (int x = 0; x < templ.image.width; ++x)
                            if (templ.image.gray[static_cast<std::size_t>(y) * templ.image.width + x]
                                == screen.image.gray[static_cast<std::size_t>(expected_y + y) * screen.image.width + expected_x + x])
                                ++equal_pixels;
                    auto const forced = UmaAssistant::Vision::find(screen.image, templ.image,
                        std::array<std::int32_t, 4>{expected_x, expected_y, templ.image.width, templ.image.height},
                        0.0, screen.image.width, screen.image.height, 1);
                    std::cerr << "  at golden coordinates equal gray pixels=" << equal_pixels << '/'
                        << templ.image.gray.size() << ", fixed ROI result=" << forced.score << " @"
                        << forced.x << ',' << forced.y << '\n';
                }
            }
        }
        if (records % 250 == 0) std::cout << "replayed " << records << " records; mismatches=" << failures << '\n';
        if (limit != 0 && records >= limit) break;
    }
    std::cout << "Golden matcher replay: " << records << " records, " << failures << " mismatches\n";
    return records == 0 || failures != 0 ? 1 : 0;
}

[[nodiscard]] GrayImage make_gray(int width, int height, std::uint8_t value = 0)
{
    return GrayImage{width, height, std::vector<std::uint8_t>(static_cast<std::size_t>(width) * height, value), {}};
}

[[nodiscard]] std::vector<std::uint8_t> decode_hex(std::string_view encoded)
{
    if ((encoded.size() & 1u) != 0) throw std::runtime_error("hex fixture has odd length");
    auto nibble = [](char value) -> std::uint8_t {
        if (value >= '0' && value <= '9') return static_cast<std::uint8_t>(value - '0');
        if (value >= 'a' && value <= 'f') return static_cast<std::uint8_t>(value - 'a' + 10);
        throw std::runtime_error("invalid hex fixture");
    };
    std::vector<std::uint8_t> result;
    result.reserve(encoded.size() / 2);
    for (std::size_t index = 0; index < encoded.size(); index += 2)
        result.push_back(static_cast<std::uint8_t>((nibble(encoded[index]) << 4) | nibble(encoded[index + 1])));
    return result;
}

} // namespace

TEST_CASE("C# FindButton real screenshot matches the recorded golden", "[vision][golden]")
{
    auto const base = std::filesystem::path(CMAKE_SOURCE_DIR) / "tests/fixtures/TemplateMatcherGolden/independent-add-skills";
    std::ifstream manifest_stream(base / "manifest.json");
    REQUIRE(manifest_stream.good());
    auto const manifest = nlohmann::json::parse(manifest_stream);
    auto const screen = load_uimg(base / manifest.at("inputs").at("screen").at("file").get<std::string>());
    auto const templ = load_uimg(base / manifest.at("inputs").at("template").at("file").get<std::string>());
    CHECK(screen.sha256 == manifest.at("inputs").at("screen").at("sha256").get<std::string>());
    CHECK(templ.sha256 == manifest.at("inputs").at("template").at("sha256").get<std::string>());
    CHECK(screen.image.gray.size() == 900u * 1600u);
    CHECK(screen.image.rgba.size() == 900u * 1600u * 4u);

    auto const expected = manifest.at("expected");
    auto const call = manifest.at("call");
    auto const actual = UmaAssistant::Vision::find_button(screen.image, templ.image,
        std::array<std::int32_t, 4>{300, 500, 300, 800}, call.at("threshold").get<double>(),
        call.at("referenceWidth").get<std::int32_t>(), call.at("referenceHeight").get<std::int32_t>());
    CHECK(actual.found == expected.at("found").get<bool>());
    CHECK(std::abs(actual.score - expected.at("score").get<double>()) <= 1.0e-6);
    CHECK(std::abs(actual.x - expected.at("x").get<int>()) <= 1);
    CHECK(std::abs(actual.y - expected.at("y").get<int>()) <= 1);
}

TEST_CASE("Color matcher accepts pure RGBA golden inputs", "[vision][color]")
{
    auto screen = make_gray(7, 7, 0);
    screen.gray.clear(); // C# color-only screenshots intentionally omit grayscale pixels.
    screen.rgba.resize(7u * 7u * 4u);
    GrayImage templ{2, 2, {}, std::vector<std::uint8_t>(16, 255)};
    for (std::size_t i = 0; i < templ.rgba.size(); i += 4)
    {
        templ.rgba[i] = 42;
        templ.rgba[i + 1] = 120;
        templ.rgba[i + 2] = 210;
        templ.rgba[i + 3] = 255;
    }
    for (int y = 2; y < 4; ++y)
        for (int x = 3; x < 5; ++x)
        {
            auto const index = (static_cast<std::size_t>(y) * 7 + x) * 4;
            std::copy_n(templ.rgba.begin(), 4, screen.rgba.begin() + static_cast<std::ptrdiff_t>(index));
        }
    auto const result = UmaAssistant::Vision::find_color(screen, templ, std::nullopt, 1.0, 7, 7);
    CHECK(result.found);
    CHECK(result.score == 1.0);
    CHECK(result.x == 3);
    CHECK(result.y == 2);
}

TEST_CASE("Gray matcher parallel scan preserves the first equal candidate", "[vision][parallel]")
{
    // This surface exceeds the managed matcher's 4M sampled-comparison threshold.
    auto const screen = make_gray(180, 100, 42);
    auto const templ = make_gray(36, 18, 42);
    auto const result = UmaAssistant::Vision::find(screen, templ, std::nullopt, 1.0, 180, 100);

    CHECK(result.found);
    CHECK(result.score == 1.0);
    CHECK(result.x == 0);
    CHECK(result.y == 0);
}

TEST_CASE("Matcher safely clamps extreme ROI and scaled dimensions", "[vision][overflow]")
{
    auto screen = make_gray(3, 3, 5);
    screen.gray[2] = 100;
    auto const templ = make_gray(1, 1, 100);
    auto const extreme_roi = std::array<std::int32_t, 4>{
        (std::numeric_limits<std::int32_t>::max)(),
        (std::numeric_limits<std::int32_t>::min)(),
        (std::numeric_limits<std::int32_t>::max)(),
        (std::numeric_limits<std::int32_t>::max)(),
    };
    auto const roi_result = UmaAssistant::Vision::find(
        screen, templ, extreme_roi, 1.0, 1, 1);
    CHECK(roi_result.found);
    CHECK(roi_result.x == 2);
    CHECK(roi_result.y == 0);

    ScaledOptions oversized{{(std::numeric_limits<double>::max)()}, 1, 1, 1, true};
    auto const scaled_result = UmaAssistant::Vision::find_scaled(
        screen, templ, std::nullopt, 0.5, 3, 3, oversized);
    CHECK_FALSE(scaled_result.found);
    CHECK(scaled_result.width == templ.width);
    CHECK(scaled_result.height == templ.height);

    auto masked_template = templ;
    masked_template.rgba.assign(4, 255);
    auto const masked_result = UmaAssistant::Vision::find_scaled_masked(
        screen, masked_template, std::nullopt, 0.5, 3, 3, oversized.scales);
    CHECK_FALSE(masked_result.found);
}

TEST_CASE("Scaled matcher preserves ties-to-even sizing and local pixel refinement", "[vision][scaled]")
{
    auto templ = make_gray(8, 8);
    for (int y = 0; y < templ.height; ++y)
        for (int x = 0; x < templ.width; ++x)
            templ.gray[static_cast<std::size_t>(y) * templ.width + x] = static_cast<std::uint8_t>((x * 31 + y * 17) % 256);
    auto screen = make_gray(32, 24, 12);
    for (int y = 0; y < 8; ++y)
        for (int x = 0; x < 8; ++x)
            screen.gray[static_cast<std::size_t>(y + 7) * screen.width + x + 11]
                = templ.gray[static_cast<std::size_t>(y) * templ.width + x];
    ScaledOptions options{{1.0}, 8, 8, 8, true};
    auto const result = UmaAssistant::Vision::find_scaled(screen, templ, std::nullopt, 0.99, 32, 24, options);
    CHECK(result.found);
    CHECK(result.score >= 0.99);
    CHECK(result.x == 11);
    CHECK(result.y == 7);
    CHECK(result.width == 8);
    CHECK(result.height == 8);
}

TEST_CASE("Masked refinement keeps its original fixed search window", "[vision][masked]")
{
    // This captured deterministic candidate surface has its coarse winner at
    // x=4, then improves through x=6 inside the original [1,7] refinement
    // window. A moving upper bound incorrectly continues to x=9.
    GrayImage templ{12, 12, decode_hex(
        "bcce14a43831550b57e6c995e8cafda995c9bb1a618a6ada72fca1d40ad85a8278ec815cc28dd59353009860fbf31d3bf1d8c26531c774c2a73456b96fe66aa46a7bbb4197984d1205818f8015d9600416028cd1f6703d78fa826b3a77019206d96ea1065378850a10e567fad6e792fd429f3acf9fe93645381d8c7310ef2e5bedbf8f651f9aa479450c6fd102cbd6ff"), {}};
    templ.rgba.resize(12u * 12u * 4u);
    for (std::size_t index = 3; index < templ.rgba.size(); index += 4)
        templ.rgba[index] = 255;
    auto const screen = GrayImage{24, 12, decode_hex(
        "5b79aa4c7fe7477ac2fab80a90216f0534a17dd913a4ee221f9988367238773dab50c2492a34b61ecc2f979bda6394ddd73566dfc77be1d0174171fda03e4e4b960690c8a1e19200a4d011e9a4e18b6f63de79146e331f6ef0f4ad187982d76f6276daa6cdf6389a2147499c012f231dd36d296028bf6d7fe34e42c8f91ec1dbe0427d19081adf70b85c8c8d1cb2aa26d4e006db8e5ad2ebef063750650bfe5ca08705231854b781e3adcab837ee76053bc1f688a2d87cc0a0f9d149395d45ece1820f5df9fa7fbcbbaf1819d8151080b41a6f0083f647bcd2fed3e10688a26f795a48bc0982cc18b9ac5d9672bfb0daad615832b1510c42c7975049c4313c5eb5c39bd6a456fe7b593049031d40a6b06f275313ddccd6cd15002021d29cc3b1"), {}};
    auto const result = UmaAssistant::Vision::find_scaled_masked(screen, templ,
        std::nullopt, 0.0, 24, 12, {1.0});
    CHECK(result.x == 6);
    CHECK(result.y == 0);
    CHECK(std::abs(result.score - 0.12770143239071075) <= 1.0e-12);
}

TEST_CASE("Meaningful and persistent change retain reset transition semantics", "[vision][change]")
{
    using UmaAssistant::Vision::has_meaningful_change;
    using UmaAssistant::Vision::has_persistent_change;
    auto before = make_gray(80, 100, 40);
    MatchResult const match{true, 0.91, 30, 45, 16, 10};
    CHECK_FALSE(has_meaningful_change(before, before, match));

    auto changed = before;
    for (int y = 8; y < 58; ++y)
        for (int x = 8; x < 70; ++x)
            changed.gray[static_cast<std::size_t>(y) * changed.width + x] = 240;
    CHECK(has_meaningful_change(before, changed, match));
    CHECK(has_persistent_change(before, changed, changed, match));

    auto pressed = before;
    for (int y = match.y; y < match.y + match.height; ++y)
        for (int x = match.x; x < match.x + match.width; ++x)
        {
            auto& pixel = pressed.gray[static_cast<std::size_t>(y) * pressed.width + x];
            pixel = static_cast<std::uint8_t>(pixel - 40);
        }
    CHECK_FALSE(has_persistent_change(before, pressed, before, match));
    auto resized = make_gray(81, 100, 40);
    CHECK(has_meaningful_change(before, resized, match));
}

int main(int argc, char* argv[])
{
    if ((argc == 3 || argc == 4) && std::string_view(argv[1]) == "--replay-corpus")
    {
        try { return replay_corpus(argv[2], argc == 4 ? static_cast<std::size_t>(std::stoull(argv[3])) : 0); }
        catch (std::exception const& exception)
        {
            std::cerr << "Golden replay failed: " << exception.what() << '\n';
            return 2;
        }
    }
    Catch::Session session;
    return session.run(argc, argv);
}

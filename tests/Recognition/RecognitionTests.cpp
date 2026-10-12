#include "WindowsOcrRecognizer.hpp"

#include <Windows.h>
#include <objbase.h>
#include <bcrypt.h>
#include <catch2/catch_all.hpp>
#include <nlohmann/json.hpp>
#include <zlib.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <cctype>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <future>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

namespace {

using UmaAssistant::Vision::GrayImage;
using UmaAssistant::Recognition::TextRecognitionResult;
using UmaAssistant::Recognition::WindowsOcrRecognizer;

[[nodiscard]] std::uint32_t read_u32(std::vector<std::uint8_t> const& bytes, std::size_t offset)
{
    if (offset > bytes.size() || bytes.size() - offset < 4) throw std::runtime_error("truncated UIMG header");
    return static_cast<std::uint32_t>(bytes[offset])
        | (static_cast<std::uint32_t>(bytes[offset + 1]) << 8)
        | (static_cast<std::uint32_t>(bytes[offset + 2]) << 16)
        | (static_cast<std::uint32_t>(bytes[offset + 3]) << 24);
}

[[nodiscard]] std::string sha256(std::vector<std::uint8_t> const& bytes)
{
    BCRYPT_ALG_HANDLE algorithm{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0)
        throw std::runtime_error("cannot open SHA-256 provider");
    struct AlgorithmGuard { BCRYPT_ALG_HANDLE handle; ~AlgorithmGuard() { BCryptCloseAlgorithmProvider(handle, 0); } } guard{algorithm};
    DWORD object_size{};
    DWORD returned{};
    if (BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&object_size),
        sizeof(object_size), &returned, 0) < 0) throw std::runtime_error("cannot query SHA-256 object size");
    std::vector<UCHAR> object(object_size);
    BCRYPT_HASH_HANDLE hash{};
    if (BCryptCreateHash(algorithm, &hash, object.data(), object_size, nullptr, 0, 0) < 0)
        throw std::runtime_error("cannot create SHA-256 hash");
    struct HashGuard { BCRYPT_HASH_HANDLE handle; ~HashGuard() { BCryptDestroyHash(handle); } } hash_guard{hash};
    if (!bytes.empty() && BCryptHashData(hash, const_cast<PUCHAR>(bytes.data()),
        static_cast<ULONG>(bytes.size()), 0) < 0) throw std::runtime_error("cannot hash OCR input");
    std::array<UCHAR, 32> result{};
    if (BCryptFinishHash(hash, result.data(), static_cast<ULONG>(result.size()), 0) < 0)
        throw std::runtime_error("cannot finish SHA-256 hash");
    constexpr char digits[] = "0123456789ABCDEF";
    std::string output;
    output.reserve(64);
    for (auto byte : result)
    {
        output.push_back(digits[byte >> 4]);
        output.push_back(digits[byte & 15]);
    }
    return output;
}

[[nodiscard]] bool equals_hash(std::string left, std::string right)
{
    auto normalize = [](std::string& value)
    {
        std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character)
        {
            return static_cast<char>(std::toupper(character));
        });
    };
    normalize(left);
    normalize(right);
    return left == right;
}

[[nodiscard]] std::vector<std::uint8_t> read_gzip(std::filesystem::path const& path)
{
    auto const narrow = path.string();
    auto* file = gzopen(narrow.c_str(), "rb");
    if (file == nullptr) throw std::runtime_error("cannot open OCR UIMG: " + narrow);
    struct Guard { gzFile file; ~Guard() { gzclose(file); } } guard{file};
    std::vector<std::uint8_t> data;
    std::array<std::uint8_t, 32768> block{};
    while (true)
    {
        auto const count = gzread(file, block.data(), static_cast<unsigned>(block.size()));
        if (count < 0) throw std::runtime_error("OCR UIMG gzip is corrupt");
        if (count == 0) break;
        data.insert(data.end(), block.begin(), block.begin() + count);
    }
    return data;
}

struct Input
{
    GrayImage image;
    std::string hash;
};

[[nodiscard]] Input read_uimg(std::filesystem::path const& path)
{
    auto const data = read_gzip(path);
    if (data.size() < 24 || data[0] != 'U' || data[1] != 'I' || data[2] != 'M' || data[3] != 'G'
        || data[4] != 1) throw std::runtime_error("bad OCR UIMG header");
    auto const width = static_cast<std::int32_t>(read_u32(data, 8));
    auto const height = static_cast<std::int32_t>(read_u32(data, 12));
    auto const gray_size = static_cast<std::int32_t>(read_u32(data, 16));
    auto const rgba_size = static_cast<std::int32_t>(read_u32(data, 20));
    if (width <= 0 || height <= 0 || gray_size < 0 || rgba_size < -1)
        throw std::runtime_error("bad OCR UIMG dimensions");
    auto const pixels = static_cast<std::uint64_t>(width) * static_cast<std::uint64_t>(height);
    if ((gray_size != 0 && static_cast<std::uint64_t>(gray_size) != pixels)
        || (rgba_size != -1 && static_cast<std::uint64_t>(rgba_size) != pixels * 4))
        throw std::runtime_error("bad OCR UIMG plane lengths");
    auto const expected_size = 24ull + static_cast<std::uint64_t>(gray_size)
        + static_cast<std::uint64_t>((std::max)(0, rgba_size));
    if (expected_size != data.size()) throw std::runtime_error("OCR UIMG is truncated or has trailing bytes");

    std::vector<std::uint8_t> hash_bytes;
    hash_bytes.reserve(16u + static_cast<std::size_t>(gray_size)
        + static_cast<std::size_t>((std::max)(0, rgba_size)));
    hash_bytes.insert(hash_bytes.end(), data.begin() + 8, data.begin() + 16);
    hash_bytes.insert(hash_bytes.end(), data.begin() + 16, data.begin() + 20);
    hash_bytes.insert(hash_bytes.end(), data.begin() + 24,
        data.begin() + static_cast<std::ptrdiff_t>(24 + gray_size));
    hash_bytes.insert(hash_bytes.end(), data.begin() + 20, data.begin() + 24);
    if (rgba_size > 0)
        hash_bytes.insert(hash_bytes.end(), data.begin() + static_cast<std::ptrdiff_t>(24 + gray_size), data.end());
    GrayImage image;
    image.width = width;
    image.height = height;
    image.gray.assign(data.begin() + 24, data.begin() + static_cast<std::ptrdiff_t>(24 + gray_size));
    if (rgba_size > 0)
        image.rgba.assign(data.begin() + static_cast<std::ptrdiff_t>(24 + gray_size), data.end());
    return {std::move(image), sha256(hash_bytes)};
}

[[nodiscard]] std::filesystem::path root_path(std::string value)
{
    std::replace(value.begin(), value.end(), '/', '\\');
    return std::filesystem::path(CMAKE_SOURCE_DIR) / value;
}

void compare_case(WindowsOcrRecognizer& recognizer, nlohmann::json const& record,
    std::filesystem::path const& baseline_root)
{
    auto const width = record.at("width").get<int>();
    auto const height = record.at("height").get<int>();
    auto const input_path = baseline_root / record.at("inputPath").get<std::string>();
    auto const input = read_uimg(input_path);
    REQUIRE(equals_hash(input.hash, record.at("inputHash").get<std::string>()));
    REQUIRE(input.image.width == width);
    REQUIRE(input.image.height == height);
    REQUIRE((input.image.gray.empty() || input.image.gray.size() == static_cast<std::size_t>(width) * height));
    REQUIRE(input.image.rgba.size() == static_cast<std::size_t>(width) * height * 4);

    std::string requested;
    if (!record.at("requestedLanguage").is_null())
        requested = record.at("requestedLanguage").get<std::string>();
    auto const result = recognizer.recognize(input.image, requested);
    CHECK(result.language == record.at("selectedLanguage").get<std::string>());
    CHECK(result.width == width);
    CHECK(result.height == height);
    auto const expected = record.at("detections");
    REQUIRE(result.detections.size() == expected.size());
    for (std::size_t index = 0; index < expected.size(); ++index)
    {
        auto const& actual = result.detections[index];
        auto const& item = expected[index];
        CHECK(actual.text == item.at("text").get<std::string>());
        CHECK(actual.bounds.x == item.at("x").get<int>());
        CHECK(actual.bounds.y == item.at("y").get<int>());
        CHECK(actual.bounds.width == item.at("width").get<int>());
        CHECK(actual.bounds.height == item.at("height").get<int>());
        CHECK(actual.confidence == item.at("confidence").get<double>());
    }
}

}

TEST_CASE("Windows OCR preserves installed-language fallback and line bounds", "[ocr][golden]")
{
    char* configured = nullptr;
    std::size_t configured_size = 0;
    static_cast<void>(_dupenv_s(&configured, &configured_size, "UMA_OCR_GOLDEN_PATH"));
    std::unique_ptr<char, decltype(&std::free)> configured_guard(configured, &std::free);
    auto const trace_path = configured == nullptr
        ? std::filesystem::path(CMAKE_SOURCE_DIR) / "build/supervisor-ocr-baseline/ocr-golden.jsonl"
        : root_path(configured);
    if (!std::filesystem::exists(trace_path)) SKIP("Set UMA_OCR_GOLDEN_PATH to the verified local OCR corpus");

    std::ifstream trace(trace_path);
    REQUIRE(trace.good());
    trace.close();
    auto const edge_path = trace_path.parent_path() / "ocr-language-edge-golden.jsonl";
    std::vector<std::filesystem::path> trace_paths{trace_path};
    if (std::filesystem::exists(edge_path) && edge_path != trace_path)
        trace_paths.push_back(edge_path);

    WindowsOcrRecognizer recognizer;
    std::size_t cases = 0;
    std::size_t expected_cases = 0;
    for (auto const& current_path : trace_paths)
    {
        std::ifstream current_trace(current_path);
        REQUIRE(current_trace.good());
        auto const current_root = current_path.parent_path();
        std::string line;
        std::size_t trace_cases = 0;
        bool summary_seen = false;
        while (std::getline(current_trace, line))
        {
            auto const record = nlohmann::json::parse(line);
            auto const type = record.value("recordType", std::string{});
            if (type == "summary")
            {
                CHECK(record.value("status", std::string{}) == "completed");
                expected_cases += record.at("cases").get<std::size_t>();
                summary_seen = true;
                continue;
            }
            if (type != "ocr") continue;
            ++trace_cases;
            ++cases;
            auto requested = std::string("<fallback>");
            if (!record.at("requestedLanguage").is_null())
                requested = record.at("requestedLanguage").get<std::string>();
            INFO("OCR case " << cases << " from " << current_path.string()
                << " requested=" << requested);
            compare_case(recognizer, record, current_root);
        }
        REQUIRE(summary_seen);
        CHECK(trace_cases <= expected_cases);
    }
    CHECK(cases == expected_cases);
    CHECK(expected_cases >= 14);
}

TEST_CASE("Windows OCR accepts processed grayscale and rejects malformed planes", "[ocr][processed]")
{
    WindowsOcrRecognizer recognizer;
    GrayImage gray{32, 32, std::vector<std::uint8_t>(32 * 32, 255), {}};
    auto rgba = gray;
    rgba.rgba.assign(32 * 32 * 4, 255);
    const auto grayResult = recognizer.recognize(gray, "en-US");
    const auto rgbaResult = recognizer.recognize(rgba, "en-US");
    CHECK(grayResult.width == rgbaResult.width);
    CHECK(grayResult.height == rgbaResult.height);
    CHECK(grayResult.language == rgbaResult.language);
    CHECK(grayResult.detections.empty());
    CHECK(rgbaResult.detections.empty());
    const auto workerResult = std::async(std::launch::async, [&] {
        return recognizer.recognize(gray, "en-US");
    }).get();
    CHECK(workerResult.width == grayResult.width);
    CHECK(workerResult.language == grayResult.language);
    CHECK(workerResult.detections.empty());
    const auto staResult = std::async(std::launch::async, [&] {
        const auto status = ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        if (FAILED(status)) throw std::runtime_error("Could not initialize the OCR test STA.");
        struct ApartmentRelease { ~ApartmentRelease() { ::CoUninitialize(); } } apartment;
        return recognizer.recognize(gray, "en-US");
    }).get();
    CHECK(staResult.width == grayResult.width);
    CHECK(staResult.language == grayResult.language);
    CHECK(staResult.detections.empty());
    gray.gray.pop_back();
    CHECK_THROWS_AS(recognizer.recognize(gray, "en-US"), std::invalid_argument);
    rgba.rgba.pop_back();
    CHECK_THROWS_AS(recognizer.recognize(rgba, "en-US"), std::invalid_argument);
}

TEST_CASE("Windows OCR survives repeated instance and worker apartment lifetimes", "[ocr][processed][CareerParity]")
{
    GrayImage image{32, 32, std::vector<std::uint8_t>(32 * 32, 255), {}};
    for (int iteration = 0; iteration < 8; ++iteration)
    {
        CAPTURE(iteration);
        {
            WindowsOcrRecognizer reader;
            auto const result = reader.recognize(image, "en-US");
            CHECK(result.width == 32);
            CHECK(result.detections.empty());
        }
        auto const result = std::async(std::launch::async, [&] {
            WindowsOcrRecognizer reader;
            return reader.recognize(image, "en-US");
        }).get();
        CHECK(result.width == 32);
        CHECK(result.detections.empty());
    }
}

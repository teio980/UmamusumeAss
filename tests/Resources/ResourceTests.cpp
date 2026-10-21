#include "Resources/ResourceSnapshot.hpp"
#include "Resources/WicImageDecoder.hpp"
#include "Resources/PipelineMetadataSchema.hpp"

#include <catch2/catch_test_macros.hpp>
#include <nlohmann/json.hpp>
#if defined(__clang__)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wundef"
#pragma clang diagnostic ignored "-Wold-style-cast"
#endif
#include <zlib.h>
#if defined(__clang__)
#pragma clang diagnostic pop
#endif

#include <algorithm>
#include <array>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <stdexcept>
#include <string>
#include <vector>

using UmaAssistant::Resources::ResourceSnapshot;

namespace {

[[nodiscard]] std::vector<std::uint8_t> read_gzip(std::filesystem::path const& path)
{
    auto const path_string = path.string();
    auto* file = gzopen(path_string.c_str(), "rb");
    if (file == nullptr) throw std::runtime_error("cannot open WIC golden sidecar");
    struct GzipGuard
    {
        gzFile file;
        ~GzipGuard() { gzclose(file); }
    } guard{file};

    std::vector<std::uint8_t> result;
    std::array<std::uint8_t, 65536> block{};
    while (true)
    {
        auto const count = gzread(file, block.data(), static_cast<unsigned>(block.size()));
        if (count < 0) throw std::runtime_error("corrupt WIC golden gzip sidecar");
        if (count == 0) break;
        result.insert(result.end(), block.begin(), block.begin() + count);
    }
    return result;
}

[[nodiscard]] std::uint32_t read_u32(std::vector<std::uint8_t> const& bytes, std::size_t offset)
{
    if (offset > bytes.size() || bytes.size() - offset < 4)
        throw std::runtime_error("truncated WIC golden UIMG header");
    return static_cast<std::uint32_t>(bytes[offset])
        | (static_cast<std::uint32_t>(bytes[offset + 1]) << 8)
        | (static_cast<std::uint32_t>(bytes[offset + 2]) << 16)
        | (static_cast<std::uint32_t>(bytes[offset + 3]) << 24);
}

struct GoldenImage
{
    std::int32_t width{};
    std::int32_t height{};
    std::vector<std::uint8_t> gray;
    std::vector<std::uint8_t> rgba;
};

[[nodiscard]] GoldenImage read_uimg(std::filesystem::path const& path)
{
    auto const data = read_gzip(path);
    if (data.size() < 24 || data[0] != 'U' || data[1] != 'I' || data[2] != 'M'
        || data[3] != 'G' || data[4] != 1)
        throw std::runtime_error("invalid WIC golden UIMG header");
    auto const width = static_cast<std::int32_t>(read_u32(data, 8));
    auto const height = static_cast<std::int32_t>(read_u32(data, 12));
    auto const gray_size = static_cast<std::int32_t>(read_u32(data, 16));
    auto const rgba_size = static_cast<std::int32_t>(read_u32(data, 20));
    if (width <= 0 || height <= 0 || gray_size < 0 || rgba_size < 0)
        throw std::runtime_error("invalid WIC golden UIMG dimensions");
    auto const pixels = static_cast<std::uint64_t>(width) * static_cast<std::uint64_t>(height);
    if (static_cast<std::uint64_t>(gray_size) != pixels || static_cast<std::uint64_t>(rgba_size) != pixels * 4
        || 24ull + static_cast<std::uint64_t>(gray_size) + static_cast<std::uint64_t>(rgba_size) != data.size())
        throw std::runtime_error("invalid WIC golden UIMG plane lengths");

    GoldenImage image{width, height};
    image.gray.assign(data.begin() + 24, data.begin() + 24 + gray_size);
    image.rgba.assign(data.begin() + 24 + gray_size, data.end());
    return image;
}

}

TEST_CASE("resource snapshot decodes and caches PNG and WebP templates with WIC", "[resources][wic]")
{
    ResourceSnapshot resources(std::filesystem::path(CMAKE_SOURCE_DIR) / "resource");

    auto const webp = resources.load_image("uma/assets/images/global/trainees/100601.webp");
    REQUIRE(webp != nullptr);
    REQUIRE(webp->width > 0);
    REQUIRE(webp->height > 0);
    REQUIRE(webp->gray.size() == static_cast<std::size_t>(webp->width) * webp->height);
    REQUIRE(webp->rgba.size() == static_cast<std::size_t>(webp->width) * webp->height * 4);
    CHECK(resources.load_image("uma/assets/images/global/trainees/100601.webp") == webp);

    auto const png = resources.load_image("hachimi/career/event/templates/inheritance_event_go.png");
    REQUIRE(png != nullptr);
    CHECK(png->width > 0);
    CHECK(png->height > 0);
    CHECK(png->rgba.size() == static_cast<std::size_t>(png->width) * png->height * 4);

    auto const definition_relative = resources.load_image_from(
        "hachimi/career/event/templates", "../../../../uma/system_reference/100101.webp");
    REQUIRE(definition_relative != nullptr);
    CHECK(definition_relative == resources.load_image("uma/system_reference/100101.webp"));
    CHECK(resources.load_image_from({}, (std::filesystem::path(CMAKE_SOURCE_DIR)
        / "resource/uma/system_reference/100101.webp").string()) != nullptr);
}

TEST_CASE("resource snapshot reports missing images", "[resources]")
{
    ResourceSnapshot resources(std::filesystem::path(CMAKE_SOURCE_DIR) / "resource");
    CHECK(resources.load_image("missing/template.png") == nullptr);
}

TEST_CASE("pipeline metadata schema keeps legacy v1 validation in Resources", "[resources][pipeline-schema]")
{
    using UmaAssistant::Resources::ReadHachimiV1PipelineMetadata;
    using Json = nlohmann::ordered_json;

    auto const legacy = ReadHachimiV1PipelineMetadata(Json::parse(R"({
        "schemaVersion": 1,
        "name": "legacy-pipeline",
        "start": "Entry",
        "description": "compatibility metadata",
        "referenceWidth": 900,
        "referenceHeight": 1600,
        "templates": {"raceResult": "result.png", "randomShop": null},
        "uma": {"charaId": "1001", "cardDeckId": "2001"},
        "timing": {
            "navigationMs": 1200, "mailboxLoadMs": 1800, "collectionSettleMs": 1200,
            "homeTimeoutMs": 5000, "homeRetryTimeoutMs": 2500, "homeVerifyTimeoutMs": 3000,
            "backAttempts": 3, "backSettleMs": 600, "pollIntervalMs": 425,
            "teamDownloadMs": 10000, "nextRaceLoadMs": 10000, "playbackLoadMs": 20000,
            "skipSettleMs": 2500, "raceTimeoutMs": 60000, "shopProbeMs": 1500,
            "betweenRacesMs": 1200
        },
        "tasks": {}
    })"));
    REQUIRE(legacy.has_value());
    CHECK(legacy->schemaVersion == 1);
    CHECK(legacy->name == "legacy-pipeline");
    CHECK(legacy->start == "Entry");
    CHECK(legacy->description == "compatibility metadata");
    CHECK(legacy->referenceWidth == 900);
    CHECK(legacy->referenceHeight == 1600);
    CHECK(legacy->pollIntervalMilliseconds == 425);

    auto const defaults = ReadHachimiV1PipelineMetadata(Json::parse(R"({"tasks":{}})"));
    REQUIRE(defaults.has_value());
    CHECK(defaults->pollIntervalMilliseconds == 300);
    CHECK(defaults->referenceWidth == 900);
    CHECK(defaults->referenceHeight == 1600);

    CHECK_FALSE(ReadHachimiV1PipelineMetadata(Json::parse(R"({"uma":{"charaId":17}})")));
    CHECK_FALSE(ReadHachimiV1PipelineMetadata(Json::parse(R"({"templates":{"unknown":"x"}})")));
    CHECK_FALSE(ReadHachimiV1PipelineMetadata(Json::parse(R"({"timing":{"unknownMs":10}})")));
    CHECK_FALSE(ReadHachimiV1PipelineMetadata(Json::parse(R"({"timing":{"raceTimeoutMs":"slow"}})")));
    CHECK_FALSE(ReadHachimiV1PipelineMetadata(Json::parse(R"({"unknownRootKey":true})")));
    CHECK_FALSE(ReadHachimiV1PipelineMetadata(Json::parse(R"({"timing":null})")));
    CHECK_FALSE(ReadHachimiV1PipelineMetadata(Json::parse(R"([])")));
}

TEST_CASE("resource snapshot shares prepared pixels and refreshes overwritten external templates", "[resources][cache]")
{
    using UmaAssistant::Resources::TemplatePreparation;
    auto const root = std::filesystem::path(CMAKE_SOURCE_DIR);
    ResourceSnapshot resources(root / "resource");
    auto const original = resources.load_template("uma/assets/images/global/trainees/100601.webp", TemplatePreparation::original);
    REQUIRE(original != nullptr);
    CHECK(resources.load_image("uma/assets/images/global/trainees/100601.webp").get() == &original->image);
    auto const head = resources.load_template("uma/assets/images/global/trainees/100601.webp", TemplatePreparation::head);
    REQUIRE(head != nullptr);
    CHECK(head == resources.load_template("uma/assets/images/global/trainees/100601.webp", TemplatePreparation::head));
    CHECK(head->image.width == 128);
    CHECK(head->image.height == 128);
    auto const path = std::filesystem::temp_directory_path() / ("uma-template-cache-"
        + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()) + ".webp");
    struct RemoveFile { std::filesystem::path path; ~RemoveFile() { std::error_code ec; std::filesystem::remove(path, ec); } } cleanup{path};
    std::filesystem::copy_file(root / "resource/uma/system_reference/100101.webp", path);
    auto const first = resources.load_image(path.string());
    REQUIRE(first != nullptr);
    auto const stamp = std::filesystem::last_write_time(path);
    std::filesystem::copy_file(root / "resource/uma/assets/images/global/trainees/100601.webp", path,
        std::filesystem::copy_options::overwrite_existing);
    std::filesystem::last_write_time(path, stamp + std::chrono::seconds(5));
    auto const second = resources.load_image(path.string());
    REQUIRE(second != nullptr);
    CHECK(second != first);
    CHECK(second->rgba == original->image.rgba);
    CHECK(first->rgba != second->rgba);
}

TEST_CASE("WIC resource decoding matches legacy pixel planes", "[resources][wic][golden]")
{
    auto const fixture_root = std::filesystem::path(CMAKE_SOURCE_DIR) / "tests/fixtures/wic";
    auto const source_root = std::filesystem::path(CMAKE_SOURCE_DIR);
    ResourceSnapshot resources(source_root / "resource");
    std::ifstream input(fixture_root / "wic-decoding-golden.jsonl", std::ios::binary);
    REQUIRE(input.good());

    std::string line;
    std::size_t cases = 0;
    while (std::getline(input, line))
    {
        auto const record = nlohmann::json::parse(line);
        if (record.value("recordType", std::string{}) != "wic_image") continue;
        ++cases;
        INFO(record.at("source").get<std::string>());
        auto relative = record.at("source").get<std::string>();
        std::replace(relative.begin(), relative.end(), '\\', '/');
        auto inputPath = record.at("inputPath").get<std::string>();
        std::replace(inputPath.begin(), inputPath.end(), '\\', '/');
        auto const expected = read_uimg(fixture_root / inputPath);
        auto const actual = resources.load_image_from({}, (source_root / relative).string());
        REQUIRE(actual != nullptr);
        CHECK(actual->width == expected.width);
        CHECK(actual->height == expected.height);
#if defined(_WIN32)
        CHECK(actual->gray == expected.gray);
        CHECK(actual->rgba == expected.rgba);
#else
        REQUIRE(actual->gray.size() == expected.gray.size());
        REQUIRE(actual->rgba.size() == expected.rgba.size());
        std::size_t maxErr = 0;
        for (std::size_t i = 0; i < actual->gray.size(); ++i)
        {
            if (expected.rgba[i * 4 + 3] == 0) continue; // Skip fully transparent pixels
            auto const diff = static_cast<std::size_t>(std::abs(static_cast<int>(actual->gray[i]) - static_cast<int>(expected.gray[i])));
            if (diff > maxErr) maxErr = diff;
        }
        CHECK(maxErr <= 5);
#endif
    }
    CHECK(cases == 10);
}

TEST_CASE("shared WIC decoder preserves file and encoded-byte image planes", "[resources][wic][bytes]")
{
    auto const path = std::filesystem::path(CMAKE_SOURCE_DIR)
        / "resource/uma/assets/images/global/trainees/100601.webp";
    std::ifstream input(path, std::ios::binary);
    REQUIRE(input.good());
    auto const encoded_size = std::filesystem::file_size(path);
    REQUIRE(encoded_size > 0);
    std::vector<std::uint8_t> encoded(static_cast<std::size_t>(encoded_size));
    input.read(reinterpret_cast<char*>(encoded.data()), static_cast<std::streamsize>(encoded.size()));
    REQUIRE(input.gcount() == static_cast<std::streamsize>(encoded.size()));

    auto const from_file = UmaAssistant::Resources::WicImageDecoder::decode_file(path);
    auto const from_bytes = UmaAssistant::Resources::WicImageDecoder::decode_bytes(encoded);
    CHECK(from_bytes.width == from_file.width);
    CHECK(from_bytes.height == from_file.height);
    CHECK(from_bytes.gray == from_file.gray);
    CHECK(from_bytes.rgba == from_file.rgba);
}

TEST_CASE("shared WIC decoder preserves raw RGBA and legacy grayscale arithmetic", "[resources][wic][rgba]")
{
    std::array<std::uint8_t, 12> const rgba{255, 0, 0, 128, 0, 255, 0, 17, 0, 0, 255, 255};
    auto const image = UmaAssistant::Resources::WicImageDecoder::from_rgba(3, 1, rgba);
    std::vector<std::uint8_t> const expected_gray{76, 149, 29};
    std::vector<std::uint8_t> const expected_rgba(rgba.begin(), rgba.end());
    CHECK(image.width == 3);
    CHECK(image.height == 1);
    CHECK(image.gray == expected_gray);
    CHECK(image.rgba == expected_rgba);
}

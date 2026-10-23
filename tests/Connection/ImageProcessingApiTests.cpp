#include "UmaAssistant/UmaCaller.h"
#include <catch2/catch_test_macros.hpp>
#include <array>
#include <cstdint>
#include <vector>
#if defined(__APPLE__)
#include <nlohmann/json.hpp>
#include <filesystem>
#include <string>
#endif

#if defined(__APPLE__)
namespace {
void UMA_CALL IgnoreOcrCallback(int32_t, const char*, void*) {}
}

TEST_CASE("Mac screenshot OCR ABI accepts gray-only images and preserves JSON bounds", "[UmaCaller][MacOcr][ocr]")
{
    REQUIRE(UmaSetUserDir(CMAKE_SOURCE_DIR) == UMA_SUCCESS);
    REQUIRE(UmaLoadResource(CMAKE_SOURCE_DIR) == UMA_SUCCESS);
    UmaHandle handle = UmaCreate(&IgnoreOcrCallback, nullptr);
    REQUIRE(handle != nullptr);
    struct HandleOwner { UmaHandle handle; ~HandleOwner() { UmaDestroy(handle); } } owner{handle};
    const auto path = std::filesystem::path(CMAKE_SOURCE_DIR)
        / "testdata/hachimi/ura/captures/career_skill_gourmand_selected_bluestacks_mac_20261023.png";
    UmaDecodedImage decoded = nullptr;
    REQUIRE(UmaDecodeImageFile(path.string().c_str(), &decoded) == UMA_SUCCESS);
    struct ImageOwner { UmaDecodedImage image; ~ImageOwner() { UmaReleaseDecodedImage(image); } } imageOwner{decoded};
    UmaDecodedImageInfo info{};
    REQUIRE(UmaGetDecodedImageInfo(decoded, &info) == UMA_SUCCESS);
    std::vector<std::uint8_t> gray(info.gray_length), rgba(info.rgba_length);
    REQUIRE(UmaCopyDecodedImage(decoded, gray.data(), gray.size(), rgba.data(), rgba.size()) == UMA_SUCCESS);
    UmaImageView source{info.width, info.height, gray.data(), gray.size(), rgba.data(), rgba.size()};
    const UmaImageTransformOptions options{625, 704, 220, 155, 0, 0, 0, 0};
    UmaDecodedImage cropped = nullptr;
    REQUIRE(UmaTransformImage(&source, &options, &cropped) == UMA_SUCCESS);
    ImageOwner cropOwner{cropped};
    REQUIRE(UmaGetDecodedImageInfo(cropped, &info) == UMA_SUCCESS);
    gray.resize(info.gray_length); rgba.resize(info.rgba_length);
    REQUIRE(UmaCopyDecodedImage(cropped, gray.data(), gray.size(), rgba.data(), rgba.size()) == UMA_SUCCESS);
    for (const bool grayscale : {false, true})
    {
        CAPTURE(grayscale);
        UmaImageView view{info.width, info.height, gray.data(), gray.size(),
            grayscale ? nullptr : rgba.data(), grayscale ? 0u : static_cast<std::uint64_t>(rgba.size())};
        std::uint64_t required = 0;
        REQUIRE(UmaRecognizeText(handle, &view, "en-US", nullptr, 0, &required) == UMA_ERROR_BUFFER_TOO_SMALL);
        REQUIRE(required > 1);
        std::vector<char> json(required);
        REQUIRE(UmaRecognizeText(handle, &view, "en-US", json.data(), json.size(), &required) == UMA_SUCCESS);
        REQUIRE(json[required - 1] == '\0');
        const auto result = nlohmann::json::parse(json.data());
        CHECK(result.at("width") == 220); CHECK(result.at("height") == 155);
        int obtained = 0;
        for (const auto& detection : result.at("detections"))
        {
            if (detection.at("text").get<std::string>().find("Obtained") != std::string::npos) ++obtained;
            const auto& bounds = detection.at("bounds");
            CHECK(bounds.at("x").get<int>() >= 0); CHECK(bounds.at("y").get<int>() >= 0);
            CHECK(bounds.at("x").get<int>() + bounds.at("width").get<int>() <= 220);
            CHECK(bounds.at("y").get<int>() + bounds.at("height").get<int>() <= 155);
        }
        CHECK(obtained == 1);
        --view.gray_length;
        CHECK(UmaRecognizeText(handle, &view, "en-US", nullptr, 0, &required) == UMA_ERROR_INVALID_ARGUMENT);
    }
}
#endif

TEST_CASE("image transforms reject malformed arguments and retain owned cropped planes", "[UmaCaller][abi][vision]")
{
    std::array<std::uint8_t, 16> const rgba{255, 0, 0, 128, 0, 255, 0, 17, 0, 0, 255, 255, 1, 2, 3, 0};
    UmaImageView source{2, 2, nullptr, 0, rgba.data(), rgba.size()};
    UmaImageTransformOptions options{1, 0, 1, 2, 0, 0, 0};
    UmaDecodedImage decoded = nullptr;
    REQUIRE(UmaTransformImage(&source, &options, &decoded) == UMA_SUCCESS);
    struct Release { UmaDecodedImage image; ~Release() { UmaReleaseDecodedImage(image); } } owner{decoded};
    UmaDecodedImageInfo info{};
    REQUIRE(UmaGetDecodedImageInfo(decoded, &info) == UMA_SUCCESS);
    CHECK(info.width == 1); CHECK(info.height == 2);
    CHECK(info.gray_length == 2); CHECK(info.rgba_length == 8);
    std::array<std::uint8_t, 2> gray{};
    std::array<std::uint8_t, 8> actual{};
    REQUIRE(UmaCopyDecodedImage(decoded, gray.data(), gray.size(), actual.data(), actual.size()) == UMA_SUCCESS);
    CHECK(gray[0] == 149); CHECK(gray[1] == 1);
    std::array<std::uint8_t, 8> const expected{0, 255, 0, 17, 1, 2, 3, 0};
    CHECK(actual == expected);
    options.crop_x = 2;
    UmaDecodedImage rejected = decoded;
    CHECK(UmaTransformImage(&source, &options, &rejected) == UMA_ERROR_INVALID_ARGUMENT);
    CHECK(rejected == nullptr);
    CHECK(UmaTransformImage(nullptr, &options, &rejected) == UMA_ERROR_INVALID_ARGUMENT);
    source.rgba_length--;
    CHECK(UmaTransformImage(&source, &options, &rejected) == UMA_ERROR_INVALID_ARGUMENT);
}

TEST_CASE("color blob ABI checks capacity before writing and supports grayscale binarization", "[UmaCaller][abi][vision]")
{
    std::array<std::uint8_t, 12> const rgba{255, 0, 0, 255, 0, 0, 0, 255, 255, 0, 0, 255};
    UmaImageView source{3, 1, nullptr, 0, rgba.data(), rgba.size()};
    UmaColorRule rule{250, 0, 0, 255, 0, 0, -255, 255, -255, 255};
    UmaImageRect region{-10, -10, 50, 50};
    std::uint64_t count = 0;
    REQUIRE(UmaFindColorBlobs(&source, &rule, &region, nullptr, 0, &count) == UMA_SUCCESS);
    REQUIRE(count == 2);
    UmaColorBlob canary{77, 77, 77, 77, 77};
    CHECK(UmaFindColorBlobs(&source, &rule, &region, &canary, 1, &count) == UMA_ERROR_BUFFER_TOO_SMALL);
    CHECK(canary.area == 77); CHECK(canary.x == 77);
    std::array<UmaColorBlob, 2> blobs{};
    REQUIRE(UmaFindColorBlobs(&source, &rule, &region, blobs.data(), blobs.size(), &count) == UMA_SUCCESS);
    CHECK(blobs[0].x == 0); CHECK(blobs[1].x == 2);
    UmaDecodedImage binary = nullptr;
    REQUIRE(UmaBinarizeColor(&source, &rule, &binary) == UMA_SUCCESS);
    struct Release { UmaDecodedImage image; ~Release() { UmaReleaseDecodedImage(image); } } owner{binary};
    std::array<std::uint8_t, 3> gray{};
    std::array<std::uint8_t, 12> pixels{};
    REQUIRE(UmaCopyDecodedImage(binary, gray.data(), gray.size(), pixels.data(), pixels.size()) == UMA_SUCCESS);
    std::array<std::uint8_t, 3> const expected{0, 255, 0};
    CHECK(gray == expected);
    CHECK(pixels[3] == 255); CHECK(pixels[7] == 255); CHECK(pixels[11] == 255);
    CHECK(UmaCountColor(nullptr, &rule, &region, nullptr, &count, nullptr) == UMA_ERROR_INVALID_ARGUMENT);
}

TEST_CASE("numeric image ABI validates placements and sample points before publishing", "[UmaCaller][abi][vision]")
{
    std::array<std::uint8_t, 3> const gray{0, 255, 17}, other{23, 255, 0};
    UmaImageView source{3, 1, gray.data(), gray.size(), nullptr, 0};
    UmaImageView after{3, 1, other.data(), other.size(), nullptr, 0};
    UmaImageRect placement{1, 1, 3, 1}; UmaDecodedImage image = nullptr;
    REQUIRE(UmaComposeGrayImages(&source, &placement, 1, 5, 3, &image) == UMA_SUCCESS);
    struct Release { UmaDecodedImage image; ~Release() { UmaReleaseDecodedImage(image); } } owner{image};
    std::array<std::uint8_t, 15> pixels{}; std::array<std::uint8_t, 60> rgba{};
    REQUIRE(UmaCopyDecodedImage(image, pixels.data(), pixels.size(), rgba.data(), rgba.size()) == UMA_SUCCESS);
    CHECK(pixels[0] == 255); CHECK(pixels[6] == 0); CHECK(pixels[7] == 255); CHECK(pixels[8] == 17);
    CHECK(rgba[6 * 4] == 0); CHECK(rgba[6 * 4 + 3] == 255);
    int groups = -1;
    REQUIRE(UmaCountInkGroups(&source, 128, &groups) == UMA_SUCCESS); CHECK(groups == 2);
    UmaImagePoint points[]{{0, 0}, {2, 0}}; std::uint64_t difference = 99;
    REQUIRE(UmaSampleImageDifference(&source, &after, points, 2, &difference) == UMA_SUCCESS); CHECK(difference == 40);
    points[1].x = 3;
    CHECK(UmaSampleImageDifference(&source, &after, points, 2, &difference) == UMA_ERROR_INVALID_ARGUMENT);
    CHECK(difference == 0);
    placement.x = 4; UmaDecodedImage rejected = image;
    CHECK(UmaComposeGrayImages(&source, &placement, 1, 5, 3, &rejected) == UMA_ERROR_INVALID_ARGUMENT);
    CHECK(rejected == nullptr);
    CHECK(UmaCountInkGroups(&source, 257, &groups) == UMA_ERROR_INVALID_ARGUMENT); CHECK(groups == 0);
    UmaColorRule rule{};
    CHECK(UmaPrepareNumericGlyphs(&source, 2, &rule, &rejected) == UMA_ERROR_INVALID_ARGUMENT);
    CHECK(rejected == nullptr);
}

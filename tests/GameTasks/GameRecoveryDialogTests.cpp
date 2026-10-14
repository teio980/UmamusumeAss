#include "GameTasks/GameRecoveryDialogDetector.hpp"
#include "Resources/ImageTransforms.hpp"
#include "Resources/WicImageDecoder.hpp"

#include <catch2/catch_test_macros.hpp>
#include <algorithm>
#include <array>
#include <filesystem>

namespace
{
using namespace UmaAssistant;
auto Root() { return std::filesystem::path(CMAKE_SOURCE_DIR) / "resource"; }
TaskEngine::TaskFrameSnapshot Frame(const Vision::GrayImage& image)
{
    return {image.width, image.height, 0, 17, 23, 29, image.gray, image.rgba};
}
}

TEST_CASE("Daily reset recognition uses the captured pixels at three device resolutions", "[GameRecovery][CapturedPixels]")
{
    Resources::ResourceSnapshot resources(Root());
    auto const original = Resources::WicImageDecoder::decode_file(Root() / "hachimi/common/date_changed/sample.png");
    for (auto const width : {900, 720, 1080})
    {
        CAPTURE(width);
        auto const frame = Frame(Resources::resize_image(original, width, width * 16 / 9));
        auto const result = GameTasks::DetectGameRecoveryDialog(&frame, &resources, Root());
        REQUIRE(result.kind == GameTasks::GameRecoveryDialogKind::dateChanged);
        REQUIRE(result.match.found);
        CHECK(result.match.score >= 0.92);
        CHECK(result.match.CenterX() >= 441);
        CHECK(result.match.CenterX() <= 459);
        CHECK(result.match.CenterY() >= 1024);
        CHECK(result.match.CenterY() <= 1072);
        CHECK(result.match.referenceWidth == 900);
        CHECK(result.match.referenceHeight == 1600);
        CHECK(result.match.frameId == 17);
        CHECK(result.match.sessionGeneration == 23);
        CHECK(result.match.geometryGeneration == 29);
    }
}

TEST_CASE("Any single Session Error marker locates its centered button at three resolutions", "[GameRecovery][CapturedPixels]")
{
    Resources::ResourceSnapshot resources(Root());
    auto const original = Resources::WicImageDecoder::decode_file(Root().parent_path()
        / "testdata/hachimi/ura/captures/session_error_inactivity_mumu_20261014.png");
    constexpr std::array markerRois{std::array{320, 475, 260, 80},
        std::array{140, 730, 620, 100}, std::array{320, 1000, 260, 90}};
    for (int retained = -1; retained < 4; ++retained)
    for (auto const width : {900, 720, 1080})
    {
        CAPTURE(retained, width);
        auto image = original;
        for (int marker = 0; marker < 3; ++marker)
        {
            if (retained == -1 || retained == marker) continue;
            auto const roi = markerRois[marker];
            for (int y = roi[1]; y < roi[1] + roi[3]; ++y)
                for (int x = roi[0]; x < roi[0] + roi[2]; ++x)
                {
                    auto const offset = static_cast<std::size_t>(y * image.width + x);
                    image.gray[offset] = 248;
                    for (std::size_t channel = 0; channel < 3; ++channel) image.rgba[offset * 4 + channel] = 248;
                }
        }
        auto const frame = Frame(Resources::resize_image(image, width, width * 16 / 9));
        auto const result = GameTasks::DetectGameRecoveryDialog(&frame, &resources, Root());
        if (retained == 3)
        {
            CHECK(result.kind == GameTasks::GameRecoveryDialogKind::none);
            continue;
        }
        REQUIRE(result.kind == GameTasks::GameRecoveryDialogKind::sessionError);
        CHECK(result.match.found);
        CHECK(result.TargetX() >= 441);
        CHECK(result.TargetX() <= 459);
        CHECK(result.TargetY() >= 1035);
        CHECK(result.TargetY() <= 1055);
        CHECK(result.match.frameId == frame.frameId);
        CHECK(result.match.sessionGeneration == frame.sessionGeneration);
        CHECK(result.match.geometryGeneration == frame.geometryGeneration);
        CHECK(result.match.referenceWidth == 900);
        CHECK(result.match.referenceHeight == 1600);
    }
}

TEST_CASE("Recovery detector rejects competing green dialogs and a reset without its OK button", "[GameRecovery][CapturedPixels]")
{
    Resources::ResourceSnapshot resources(Root());
    for (auto const name : {"rest_confirmation.png", "infirmary_confirm.png", "recreation_confirmation.png",
            "summer_rest_dialog.png", "career_skill_learn_sample.png", "career_skill_obtained_sample.png"})
    {
        CAPTURE(name);
        auto const frame = Frame(Resources::WicImageDecoder::decode_file(Root() / "hachimi/ura/screens/captures" / name));
        CHECK(GameTasks::DetectGameRecoveryDialog(&frame, &resources, Root()).kind
            == GameTasks::GameRecoveryDialogKind::none);
    }
    auto image = Resources::WicImageDecoder::decode_file(Root() / "hachimi/common/date_changed/sample.png");
    for (int y = 975; y < 1105; ++y)
        for (int x = 250; x < 650; ++x)
        {
            auto const offset = static_cast<std::size_t>(y * image.width + x);
            image.gray[offset] = 248;
            for (std::size_t channel = 0; channel < 3; ++channel) image.rgba[offset * 4 + channel] = 248;
        }
    auto const frame = Frame(image);
    CHECK(GameTasks::DetectGameRecoveryDialog(&frame, &resources, Root()).kind
        == GameTasks::GameRecoveryDialogKind::none);
}

TEST_CASE("Connection Error requires both original title and Title Screen button lettering", "[GameRecovery][CapturedPixels]")
{
    Resources::ResourceSnapshot resources(Root());
    Vision::GrayImage fixture{900, 1600, std::vector<std::uint8_t>(900U * 1600U, 248),
        std::vector<std::uint8_t>(900U * 1600U * 4U, 248)};
    for (std::size_t i = 3; i < fixture.rgba.size(); i += 4) fixture.rgba[i] = 255;
    auto paste = [&](const char* name, int x, int y) {
        auto const crop = Resources::WicImageDecoder::decode_file(Root() / "hachimi/common/date_changed" / name);
        for (int row = 0; row < crop.height; ++row)
        {
            auto const destination = static_cast<std::size_t>((y + row) * fixture.width + x);
            auto const source = static_cast<std::size_t>(row * crop.width);
            std::copy_n(crop.gray.begin() + static_cast<std::ptrdiff_t>(source), crop.width,
                fixture.gray.begin() + static_cast<std::ptrdiff_t>(destination));
            std::copy_n(crop.rgba.begin() + static_cast<std::ptrdiff_t>(source * 4), crop.width * 4,
                fixture.rgba.begin() + static_cast<std::ptrdiff_t>(destination * 4));
        }
    };
    paste("err.png", 320, 500);
    auto frame = Frame(fixture);
    CHECK(GameTasks::DetectGameRecoveryDialog(&frame, &resources, Root()).kind
        == GameTasks::GameRecoveryDialogKind::none);
    paste("titlescreen.png", 160, 1028);
    for (auto const width : {900, 720, 1080})
    {
        CAPTURE(width);
        frame = Frame(Resources::resize_image(fixture, width, width * 16 / 9));
        auto const result = GameTasks::DetectGameRecoveryDialog(&frame, &resources, Root());
        REQUIRE(result.kind == GameTasks::GameRecoveryDialogKind::connectionError);
        CHECK(result.match.CenterX() >= 249);
        CHECK(result.match.CenterX() <= 253);
        CHECK(result.match.CenterY() >= 1039);
        CHECK(result.match.CenterY() <= 1043);
    }
}

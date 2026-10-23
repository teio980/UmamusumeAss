#if defined(__APPLE__)
#include "WindowsOcrRecognizer.hpp"
#include "Resources/WicImageDecoder.hpp"
#include "Resources/ImageTransforms.hpp"
#include <catch2/catch_all.hpp>

#include <algorithm>
#include <cctype>
#include <filesystem>
#include <string>
#include <array>
#include <vector>

namespace {
using UmaAssistant::Recognition::WindowsOcrRecognizer;
using UmaAssistant::Recognition::TextRecognitionResult;

auto LoadCapture(const char* name)
{
    return UmaAssistant::Resources::WicImageDecoder::decode_file(
        std::filesystem::path(CMAKE_SOURCE_DIR) / "testdata/hachimi/ura/captures" / name);
}

std::string FoldText(std::string text)
{
    for (auto& ch : text) ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
    return text;
}

int CountText(const TextRecognitionResult& result, const std::string& expected)
{
    return static_cast<int>(std::count_if(result.detections.begin(), result.detections.end(), [&](const auto& line) {
        return FoldText(line.text).find(FoldText(expected)) != std::string::npos;
    }));
}

void CheckBounds(const TextRecognitionResult& result)
{
    for (const auto& line : result.detections)
    {
        CHECK(line.bounds.x >= 0);
        CHECK(line.bounds.y >= 0);
        CHECK(line.bounds.width > 0);
        CHECK(line.bounds.height > 0);
        CHECK(line.bounds.x + line.bounds.width <= result.width);
        CHECK(line.bounds.y + line.bounds.height <= result.height);
        CHECK(line.confidence >= 0.0);
        CHECK(line.confidence <= 1.0);
    }
}
}

TEST_CASE("Mac OCR retains selected gold skill names without duplicate rows", "[MacOcr][RealCapture]")
{
    WindowsOcrRecognizer reader;
    for (const auto* file : {"career_skill_gourmand_before_bluestacks_mac_20261023.png",
        "career_skill_gourmand_selected_bluestacks_mac_20261023.png"})
    {
        CAPTURE(file);
        const auto image = LoadCapture(file);
        const auto list = UmaAssistant::Resources::crop_image(image, 155, 590, 475, 690);
        const auto result = reader.recognize(list, "en-US");
        CHECK(CountText(result, "Gourmand") == 1);
        CHECK(CountText(result, "Hydrate") == 1);
        CHECK(CountText(result, "A Small Breather") == 1);
        CheckBounds(result);
        for (const auto& line : result.detections)
            if (FoldText(line.text) == "gourmand") CHECK(line.bounds.y == Catch::Approx(129).margin(4));
    }
}

TEST_CASE("Mac OCR distinguishes a selected skill badge from its unselected price", "[MacOcr][RealCapture]")
{
    WindowsOcrRecognizer reader;
    const auto before = LoadCapture("career_skill_gourmand_before_bluestacks_mac_20261023.png");
    const auto selected = LoadCapture("career_skill_gourmand_selected_bluestacks_mac_20261023.png");
    const auto beforeResult = reader.recognize(UmaAssistant::Resources::crop_image(before, 625, 704, 220, 155), "en-US");
    CHECK(CountText(beforeResult, "obtained") == 0);
    const auto selectedResult = reader.recognize(UmaAssistant::Resources::crop_image(selected, 625, 704, 220, 155), "en-US");
    CHECK(CountText(selectedResult, "obtained") == 1);
    CheckBounds(selectedResult);
}

TEST_CASE("Mac OCR reads colored mood badges and isolated countdown digits", "[MacOcr][RealCapture]")
{
    WindowsOcrRecognizer reader;
    const auto image = LoadCapture("career_main_normal_bluestacks_mac_20261023.png");
    const auto mood = reader.recognize(UmaAssistant::Resources::crop_image(image, 654, 169, 170, 68), "en-US");
    CHECK(CountText(mood, "normal") == 1);
    CheckBounds(mood);
    const auto digits = reader.recognize(UmaAssistant::Resources::crop_image(image, 25, 120, 140, 78), "en-US");
    REQUIRE(digits.detections.size() == 1);
    CHECK(digits.detections.front().text == "7");
    CheckBounds(digits);
    // Reusing one engine for different layouts must not leak segmentation or
    // adaptive state into the next reading.
    const auto moodAgain = reader.recognize(UmaAssistant::Resources::crop_image(image, 654, 169, 170, 68), "en-US");
    CHECK(CountText(moodAgain, "normal") == 1);
}

TEST_CASE("Mac OCR leaves uniform images empty in every input plane", "[MacOcr]")
{
    WindowsOcrRecognizer reader;
    for (const auto value : {std::uint8_t{0}, std::uint8_t{128}, std::uint8_t{255}})
    {
        UmaAssistant::Vision::GrayImage image;
        image.width = 140;
        image.height = 78;
        image.gray.assign(140 * 78, value);
        image.rgba.assign(140 * 78 * 4, value);
        for (std::size_t i = 3; i < image.rgba.size(); i += 4) image.rgba[i] = 255;
        CHECK(reader.recognize(image, "en-US").detections.empty());
        image.rgba.clear();
        CHECK(reader.recognize(image, "en-US").detections.empty());
    }
}

TEST_CASE("Mac OCR keeps selected skill rows across emulator scales and grayscale input", "[MacOcr][RealCapture]")
{
    WindowsOcrRecognizer reader;
    const auto image = LoadCapture("career_skill_gourmand_selected_bluestacks_mac_20261023.png");
    const auto list = UmaAssistant::Resources::crop_image(image, 155, 590, 475, 690);
    for (const auto scale : {0.8, 1.0, 1.2})
    {
        CAPTURE(scale);
        auto resized = UmaAssistant::Resources::resize_image(list,
            static_cast<int>(475 * scale), static_cast<int>(690 * scale));
        for (const bool grayscale : {false, true})
        {
            CAPTURE(grayscale);
            if (grayscale) resized.rgba.clear();
            const auto result = reader.recognize(resized, "en-US");
            CHECK(CountText(result, "Gourmand") == 1);
            CHECK(CountText(result, "Hydrate") == 1);
            CHECK(CountText(result, "A Small Breather") == 1);
            CheckBounds(result);
        }
    }
}

TEST_CASE("Mac OCR covers entry events races agendas shops and independent training", "[MacOcr][OcrCorpus]")
{
    struct Fixture { const char* path; std::array<int, 4> roi; std::vector<std::string> expected; };
    const std::vector<Fixture> fixtures{
        {"tests/UmamusumeWpfGui.Tests/Fixtures/Shop/daily-sales-alarm-and-parfait.png",
            {175, 585, 520, 565}, {"Alarm Clock", "Pleasing Parfait"}},
        {"testdata/hachimi/ura/captures/ura_agenda.png",
            {20, 220, 850, 1090}, {"Early Jan", "Late Mar", "Early Oct", "Late Dec"}},
        {"testdata/hachimi/ura/captures/scenario_event_duel_choice_result_ura.png",
            {140, 295, 570, 70}, {"Happy Meek's Challenge"}},
        {"testdata/hachimi/ura/captures/scenario_event_duel_choice_result_ura.png",
            {90, 1180, 720, 160}, {"Power cap went up by 4", "Power went up by 10", "Skill Pts went up by 30"}},
        {"testdata/hachimi/ura/captures/year2_nhk_race_confirm.png",
            {20, 830, 860, 110}, {"Tokyo Turf 1600m", "Classic Year May"}},
        {"testdata/hachimi/ura/captures/debut_race_results_screen.png",
            {370, 250, 300, 1060}, {"Bow and Shield", "Swift Axel", "Mint Drop", "Oguri Cap", "Pleasant Clerk", "Pastime Joy"}},
        {"testdata/hachimi/ura/captures/support_select_mumu_guest_empty_20261012.png",
            {50, 200, 800, 200}, {"Formation"}},
        {"testdata/hachimi/ura/captures/legacy_select_ura.png",
            {10, 195, 380, 55}, {"Legacy Select"}},
        {"testdata/hachimi/ura/captures/career_home_training_independently.png",
            {520, 1200, 345, 70}, {"Training Independently"}},
        {"testdata/hachimi/ura/captures/event_home_mumu_20261016.png",
            {200, 960, 600, 120}, {"Return to the event home screen", "Collective Rewards"}},
        {"testdata/hachimi/ura/captures/rewards_collected_bluestacks_mac_20261023.png",
            {0, 0, 900, 1600}, {"Rewards Collected", "Book of Hints", "Monies", "5,000",
                "Support Points", "3,000", "Alarm Clock", "Friend Points", "600", "Close"}},
        {"testdata/hachimi/ura/captures/career_epithet_bluestacks_mac_20261023.png",
            {360, 40, 180, 80}, {"Epithet"}},
    };
    auto normalize = [](std::string text) {
        std::string normalized;
        for (const unsigned char ch : text)
            if (std::isalnum(ch)) normalized.push_back(static_cast<char>(std::tolower(ch)));
        return normalized;
    };
    WindowsOcrRecognizer reader;
    for (const auto& fixture : fixtures)
    {
        CAPTURE(fixture.path);
        const auto image = UmaAssistant::Resources::WicImageDecoder::decode_file(
            std::filesystem::path(CMAKE_SOURCE_DIR) / fixture.path);
        const auto& r = fixture.roi;
        const auto result = reader.recognize(UmaAssistant::Resources::crop_image(image, r[0], r[1], r[2], r[3]), "en-US");
        std::string text;
        for (const auto& line : result.detections) text += line.text + " ";
        CAPTURE(text);
        for (const auto& expected : fixture.expected)
        {
            CAPTURE(expected);
            CHECK(normalize(text).find(normalize(expected)) != std::string::npos);
        }
        CheckBounds(result);
    }
}
#endif

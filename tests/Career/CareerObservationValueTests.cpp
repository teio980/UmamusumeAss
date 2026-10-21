#include "UmaAssistantCore/Career/CareerObservationReader.hpp"
#include <catch2/catch_test_macros.hpp>
#include <catch2/catch_approx.hpp>
#include <array>
#include <limits>

using UmaAssistant::Career::CareerObservationReader;

TEST_CASE("Native Career calendar retains managed OCR vocabulary and boundaries", "[Career][Parity]")
{
    std::array const cases{
        std::pair{"Junior Year Pre-Debut", 0}, std::pair{"Junior Year Early Jan", 1},
        std::pair{"Junior Year Late Jul", 14}, std::pair{"Classic Year Early Jan", 25},
        std::pair{"SeniorYear Early Jan", 49}, std::pair{"Senior Year Late Dec", 72},
        std::pair{"Junior Year Late Jui", 14}, std::pair{"Junior Year Late Iul", 14},
        std::pair{"Classic Year Late July", 38}, std::pair{"Senior Year Early September", 65}};
    for (auto const& [text, index] : cases)
    {
        CAPTURE(text);
        auto const position = CareerObservationReader::ParseTurnPosition(text);
        REQUIRE(position);
        CHECK(position->turnIndex == index);
    }
    for (auto const* invalid : {"", "Junior Year", "Career", "pre-debut", "Junior Year Jan",
        "Junior next race July", "Junior Year Early UnknownMonth"})
    {
        CAPTURE(invalid);
        CHECK_FALSE(CareerObservationReader::ParseTurnPosition(invalid));
    }
}

TEST_CASE("Native mood parsing preserves managed mixed-label priority", "[Career][Parity]")
{
    CHECK(CareerObservationReader::ParseMood("Great! Awful") == "awful");
    CHECK(CareerObservationReader::ParseMood("G.o.o.d") == "good");
    CHECK(CareerObservationReader::ParseMood("bad NORMAL") == "bad");
    CHECK_FALSE(CareerObservationReader::ParseMood("?"));
}

TEST_CASE("Career energy honors profile color thresholds and pixel bounds", "[Career][Parity]")
{
    constexpr int width = 40, height = 5;
    std::vector<std::uint8_t> pixels(static_cast<std::size_t>(width * height * 4), 255);
    for (int row = 0; row < height; ++row)
        for (int column = 0; column < width; ++column)
        {
            auto const offset = static_cast<std::size_t>((row * width + column) * 4);
            // Left half mildly saturated green, remaining half neutral background.
            pixels[offset] = column < 20 ? 160 : 100;
            pixels[offset + 1] = column < 20 ? 200 : 100;
            pixels[offset + 2] = column < 20 ? 160 : 100;
        }
    auto const read = [&](double saturation, std::size_t bytes) {
        return CareerObservationReader::MeasureEnergyBar(pixels.data(), width, height,
            {0,0,width,height}, {}, width, height, saturation, 0.18, 0.55, bytes);
    };
    auto const normal = read(0.18, pixels.size());
    REQUIRE(normal);
    CHECK(normal->percent == 50);
    CHECK(normal->confidence == Catch::Approx(1.0));
    CHECK_FALSE(read(0.8, pixels.size()));
    CHECK_FALSE(read(0.18, pixels.size() - 1));
    auto const nanDefault = read((std::numeric_limits<double>::quiet_NaN)(), pixels.size());
    REQUIRE(nanDefault);
    CHECK(nanDefault->percent == normal->percent);
}

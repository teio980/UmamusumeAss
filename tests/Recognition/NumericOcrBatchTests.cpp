#include "NumericOcrBatch.hpp"

#include "../../src/UmaAssistantCore/Connection/AdbCommandRunner.hpp"
#include "../../src/UmaAssistantCore/Common/PathEncoding.hpp"

#include <catch2/catch_all.hpp>

#include <array>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <functional>
#include <map>
#include <stop_token>
#include <string>
#include <system_error>
#include <utility>
#include <vector>

namespace {

using UmaAssistant::IWin32Process;
using UmaAssistant::Recognition::NumericOcrBatch;
using UmaAssistant::Recognition::NumericOcrBatchField;
using UmaAssistant::Vision::GrayImage;

[[nodiscard]] GrayImage crop_with_ink_groups(int groups)
{
    GrayImage image;
    image.width = 20;
    image.height = 10;
    image.gray.assign(static_cast<std::size_t>(image.width * image.height), 255);
    std::array const columns{3, 8, 14};
    for (int group = 0; group < groups; ++group)
        for (int y = 2; y < 8; ++y)
            image.gray[static_cast<std::size_t>(y * image.width + columns[static_cast<std::size_t>(group)])] = 0;
    return image;
}

[[nodiscard]] std::string tsv(std::map<int, std::string> const& pages)
{
    std::string result = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n";
    for (auto const& [page, text] : pages)
    {
        result += "5\t" + std::to_string(page) + "\t1\t1\t1\t1\t0\t0\t20\t10\t95\t" + text + "\n";
    }
    return result;
}

[[nodiscard]] IWin32Process::Result success(std::string output)
{
    IWin32Process::Result result;
    result.exit_code = 0;
    result.standard_output = std::move(output);
    result.started = true;
    return result;
}

struct ObservedImage
{
    std::filesystem::path path;
    std::uint32_t width{};
    std::uint32_t height{};
    bool exists{};
};

class FakeWin32Process final : public IWin32Process
{
public:
    std::vector<Result> results;
    std::vector<std::wstring> command_lines;
    std::vector<std::chrono::milliseconds> timeouts;
    std::vector<std::string> psms;
    std::vector<std::vector<std::string>> image_lists;
    std::vector<ObservedImage> images;
    std::function<void()> before_execute;

    [[nodiscard]] Result execute(
        std::filesystem::path const& executable,
        std::wstring const& command_line,
        std::stop_token cancellation,
        std::chrono::milliseconds timeout,
        std::size_t max_standard_output_bytes,
        bool leave_children_running_on_success) override
    {
        CHECK(executable == expected_executable);
        CHECK(max_standard_output_bytes > 0);
        CHECK_FALSE(leave_children_running_on_success);
        command_lines.push_back(command_line);
        timeouts.push_back(timeout);

        auto const psm_position = command_line.find(L"--psm ");
        if (psm_position != std::wstring::npos)
        {
            auto const value_start = psm_position + 6;
            auto const value_end = command_line.find(L' ', value_start);
            auto const value = command_line.substr(value_start,
                value_end == std::wstring::npos ? value_end : value_end - value_start);
            psms.push_back(UmaAssistant::Paths::ToUtf8(std::filesystem::path(value)));
        }

        std::size_t cursor = 0;
        std::size_t executable_end = std::wstring::npos;
        if (!command_line.empty() && command_line.front() == L'"')
        {
            executable_end = command_line.find(L'"', 1);
            cursor = executable_end == std::wstring::npos ? command_line.size() : executable_end + 1;
        }
        else
        {
            executable_end = command_line.find_first_of(L" \t");
            cursor = executable_end == std::wstring::npos ? command_line.size() : executable_end + 1;
        }
        while (cursor < command_line.size() && (command_line[cursor] == L' ' || command_line[cursor] == L'\t'))
            ++cursor;
        bool const list_is_quoted = cursor < command_line.size() && command_line[cursor] == L'"';
        if (list_is_quoted) ++cursor;
        auto const list_end = list_is_quoted
            ? command_line.find(L'"', cursor) : command_line.find_first_of(L" \t", cursor);
        if (executable_end == std::wstring::npos || cursor >= command_line.size()
            || list_end == std::wstring::npos)
        {
            image_lists.emplace_back();
        }
        else
        {
            std::filesystem::path list_path(command_line.substr(cursor, list_end - cursor));
            std::ifstream list_file(list_path, std::ios::binary);
            std::vector<std::string> listed;
            std::string line;
            while (std::getline(list_file, line))
            {
                listed.push_back(line);
                auto const image_path = UmaAssistant::Paths::FromUtf8(line);
                ObservedImage image;
                image.path = image_path;
                image.exists = std::filesystem::is_regular_file(image_path);
                if (image.exists)
                {
                    std::ifstream png(image_path, std::ios::binary);
                    std::array<unsigned char, 24> header{};
                    png.read(reinterpret_cast<char*>(header.data()), static_cast<std::streamsize>(header.size()));
                    if (png.gcount() == static_cast<std::streamsize>(header.size())
                        && header[0] == 0x89 && header[1] == 'P' && header[2] == 'N' && header[3] == 'G')
                    {
                        image.width = (static_cast<std::uint32_t>(header[16]) << 24)
                            | (static_cast<std::uint32_t>(header[17]) << 16)
                            | (static_cast<std::uint32_t>(header[18]) << 8) | header[19];
                        image.height = (static_cast<std::uint32_t>(header[20]) << 24)
                            | (static_cast<std::uint32_t>(header[21]) << 16)
                            | (static_cast<std::uint32_t>(header[22]) << 8) | header[23];
                    }
                }
                images.push_back(std::move(image));
            }
            image_lists.push_back(std::move(listed));
        }

        if (before_execute) before_execute();
        if (cancellation.stop_requested())
        {
            Result canceled;
            canceled.canceled = true;
            return canceled;
        }
        auto const index = command_lines.size() - 1;
        return index < results.size() ? results[index] : Result{};
    }

    std::filesystem::path expected_executable{L"C:\\fake tools\\tesseract.exe"};
};

[[nodiscard]] std::vector<NumericOcrBatchField> two_gain_fields()
{
    return {
        {"first", crop_with_ink_groups(2), 999, true},
        {"second", crop_with_ink_groups(3), 999, true},
    };
}

} // namespace

TEST_CASE("Numeric OCR keeps TSV page numbers attached to their original crops")
{
    FakeWin32Process process;
    process.results = {success(tsv({{1, " \xC2\xA0"}, {2, "+11"}})), success(tsv({{1, "+7"}}))};
    NumericOcrBatch service(process, process.expected_executable);
    auto const fields = two_gain_fields();

    auto const readings = service.read(fields);

    REQUIRE(readings.size() == 2);
    CHECK(readings.at("first").value == 7);
    CHECK(readings.at("second").value == 11);
    CHECK(readings.at("first").rawText == "+7");
    CHECK(readings.at("second").rawText == "+11");
    REQUIRE((process.psms == std::vector<std::string>{"7", "8"}));
    REQUIRE(process.image_lists.size() == 2);
    CHECK(process.image_lists[0].size() == 2);
    CHECK(process.image_lists[1].size() == 1);
    CHECK(process.timeouts[0] > std::chrono::milliseconds::zero());
    CHECK(process.timeouts[0] <= std::chrono::seconds(5));
    REQUIRE(process.images.size() == 3);
    CHECK(process.images[0].exists);
    CHECK(process.images[0].width == 40);
    CHECK(process.images[0].height == 20);
    CHECK_FALSE(std::filesystem::exists(process.images[0].path));
}

TEST_CASE("Numeric OCR tries PSM modes then pads only unresolved gain crops")
{
    FakeWin32Process process;
    process.results = {
        success(tsv({{2, "10%"}})),
        success(tsv({})),
        success(tsv({})),
        success(tsv({{1, "+22"}})),
    };
    NumericOcrBatch service(process, process.expected_executable);
    std::vector<NumericOcrBatchField> fields{
        {"gain", crop_with_ink_groups(3), 999, true},
        {"failure_rate", crop_with_ink_groups(3), 100, false},
    };

    auto const readings = service.read(fields);

    CHECK(readings.at("gain").value == 22);
    CHECK(readings.at("failure_rate").value == 10);
    CHECK((process.psms == std::vector<std::string>{"7", "8", "13", "7"}));
    REQUIRE(process.image_lists.size() == 4);
    CHECK(process.image_lists[0].size() == 2);
    CHECK(process.image_lists[1].size() == 1);
    CHECK(process.image_lists[2].size() == 1);
    REQUIRE(process.image_lists[3].size() == 1);
    CHECK(process.image_lists[3][0].find("-padded.png") != std::string::npos);
    CHECK(process.images.back().width == 64);
    CHECK(process.images.back().height == 44);
}

TEST_CASE("Countdown numeric OCR accepts unsigned integers without changing training token parsing",
    "[CareerStartupMainRegression]")
{
    using UmaAssistant::Recognition::NumericOcrFormat;
    for (auto const& [raw, expected] : std::vector<std::pair<std::string, std::optional<int>>>{
        {"0", 0}, {"11", 11}, {"'11", 11}, {"\xE2\x80\x98" "11", 11}, {"100", 100}, {"2147483647", 2147483647},
        {"2147483648", std::nullopt}, {"11%", std::nullopt}, {"+11", std::nullopt},
        {"-11", std::nullopt}, {"11 12", std::nullopt}, {"Failure 11", std::nullopt},
        {"II", std::nullopt}, {"", std::nullopt}})
    {
        CAPTURE(raw);
        FakeWin32Process process;
        process.results.assign(3, success(tsv({{1, raw}})));
        NumericOcrBatch service(process, process.expected_executable);
        std::vector<NumericOcrBatchField> fields{{"turns", crop_with_ink_groups(2), 2147483647, false,
            NumericOcrFormat::UnsignedInteger}};
        CHECK(service.read(fields).at("turns").value == expected);
    }
}

TEST_CASE("Numeric OCR rejects gain values whose crop glyph count disagrees")
{
    FakeWin32Process process;
    process.results = {
        success(tsv({{1, "+11"}})),
        success(tsv({{1, "+11"}})),
        success(tsv({{1, "+11"}})),
        success(tsv({{1, "+11"}})),
    };
    NumericOcrBatch service(process, process.expected_executable);
    std::vector<NumericOcrBatchField> fields{{"ambiguous", crop_with_ink_groups(2), 999, true}};

    auto const readings = service.read(fields);

    CHECK_FALSE(readings.at("ambiguous").value.has_value());
    CHECK(readings.at("ambiguous").glyphMismatch);
    CHECK(readings.at("ambiguous").rawText == "+11 | +11 | +11 | +11");
    CHECK((process.psms == std::vector<std::string>{"7", "8", "13", "7"}));
}

TEST_CASE("Numeric OCR number parsing follows training OCR tokens")
{
    SECTION("removes Failure safely at short and trailing tokens and normalizes OCR text")
    {
        FakeWin32Process process;
        process.results = {success(tsv({
            {1, "Failure + O8"}, {2, "+\xC2\xA0O8"}, {3, "+lI"},
            {4, "+19"}, {5, "+9 Failure"}}))};
        NumericOcrBatch service(process, process.expected_executable);
        std::vector<NumericOcrBatchField> fields{
            {"label", crop_with_ink_groups(3), 999, false},
            {"space", crop_with_ink_groups(3), 999, false},
            {"letter_digits", crop_with_ink_groups(3), 999, false},
            {"short_token", crop_with_ink_groups(3), 999, false},
            {"trailing_token", crop_with_ink_groups(3), 999, false},
        };

        auto const readings = service.read(fields);

        CHECK(readings.at("label").value == 8);
        CHECK(readings.at("space").value == 8);
        CHECK(readings.at("letter_digits").value == 11);
        CHECK(readings.at("short_token").value == 19);
        CHECK(readings.at("trailing_token").value == 9);
    }

    SECTION("rejects negative gains, malformed fragments, and a percentage without percent")
    {
        FakeWin32Process process;
        auto const invalid = tsv({{1, "- 5"}, {2, "\xE2\x88\x92\xC2\xA0" "5"},
            {3, "+fi]9"}, {4, "010"}});
        process.results = {success(invalid), success(invalid), success(invalid), success(invalid)};
        NumericOcrBatch service(process, process.expected_executable);
        std::vector<NumericOcrBatchField> fields{
            {"negative", crop_with_ink_groups(3), 999, false},
            {"unicode_negative", crop_with_ink_groups(3), 999, false},
            {"malformed", crop_with_ink_groups(3), 999, false},
            {"percentage", crop_with_ink_groups(3), 100, false},
        };

        auto const readings = service.read(fields);

        CHECK_FALSE(readings.at("negative").value.has_value());
        CHECK_FALSE(readings.at("unicode_negative").value.has_value());
        CHECK_FALSE(readings.at("malformed").value.has_value());
        CHECK_FALSE(readings.at("percentage").value.has_value());
    }
}

TEST_CASE("Numeric OCR treats Tesseract failures and a frame timeout as unknown")
{
    SECTION("an unsuccessful process can be followed by a successful PSM")
    {
        FakeWin32Process process;
        IWin32Process::Result unavailable;
        process.results = {unavailable, success(tsv({{1, "+9"}}))};
        NumericOcrBatch service(process, process.expected_executable);
        std::vector<NumericOcrBatchField> fields{{"gain", crop_with_ink_groups(3), 999, false}};

        auto const readings = service.read(fields);

        CHECK(readings.at("gain").value == 9);
        CHECK((process.psms == std::vector<std::string>{"7", "8"}));
    }

    SECTION("a timed out process ends the five second frame budget")
    {
        FakeWin32Process process;
        IWin32Process::Result timed_out;
        timed_out.started = true;
        timed_out.timed_out = true;
        process.results = {timed_out, success(tsv({{1, "+9"}}))};
        NumericOcrBatch service(process, process.expected_executable);
        std::vector<NumericOcrBatchField> fields{{"gain", crop_with_ink_groups(3), 999, false}};

        auto const readings = service.read(fields);

        CHECK_FALSE(readings.at("gain").value.has_value());
        CHECK(process.psms.size() == 1);
        CHECK(process.timeouts.front() <= std::chrono::seconds(5));
        REQUIRE_FALSE(process.images.empty());
        CHECK_FALSE(std::filesystem::exists(process.images.front().path));
    }
}

TEST_CASE("Numeric OCR skips a missing executable and propagates caller cancellation")
{
    SECTION("missing executable returns unknown values without starting a process")
    {
        FakeWin32Process process;
        NumericOcrBatch service(process, {});
        std::vector<NumericOcrBatchField> fields{{"gain", crop_with_ink_groups(3), 999, false}};

        auto const readings = service.read(fields);

        CHECK_FALSE(readings.at("gain").value.has_value());
        CHECK(process.command_lines.empty());
    }

    SECTION("a requested stop cancels the process and returns cancellation")
    {
        FakeWin32Process process;
        process.results = {success(tsv({{1, "+9"}}))};
        std::stop_source cancellation;
        process.before_execute = [&cancellation] { cancellation.request_stop(); };
        NumericOcrBatch service(process, process.expected_executable);
        std::vector<NumericOcrBatchField> fields{{"gain", crop_with_ink_groups(3), 999, false}};

        bool did_cancel = false;
        try
        {
            (void)service.read(fields, cancellation.get_token());
        }
        catch (std::system_error const& exception)
        {
            did_cancel = exception.code() == std::make_error_code(std::errc::operation_canceled);
        }
        CHECK(did_cancel);
        CHECK(process.command_lines.size() == 1);
        REQUIRE_FALSE(process.images.empty());
        CHECK_FALSE(std::filesystem::exists(process.images.front().path));
    }
}


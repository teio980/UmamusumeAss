#include "NumericOcrBatch.hpp"
#include "NumericOcrProtocol.hpp"
#include "Connection/AdbCommandRunner.hpp"
#include "Common/PathEncoding.hpp"
#include "Resources/WicImageDecoder.hpp"
#include "Vision/NumericImageProcessing.hpp"

#if defined(_WIN32)
#include <Windows.h>
#endif
#include <catch2/catch_all.hpp>
#include <nlohmann/json.hpp>

#include <chrono>
#include <algorithm>
#include <array>
#include <iostream>
#include <memory>
#include <stop_token>
#include <string>
#include <thread>
#include <vector>

namespace {

using nlohmann::json;
using namespace UmaAssistant::Recognition;
using UmaAssistant::IWin32InteractiveSession;
using UmaAssistant::IWin32Process;

struct WorkerTrace
{
    std::vector<json> requests;
    std::vector<std::vector<std::string>> replies;
    std::function<void()> beforeWrite;
    bool closed{};
    bool malformed{};
    bool stall{};
    bool unavailable{};
};

class FakeWorker final : public IWin32InteractiveSession
{
public:
    explicit FakeWorker(std::shared_ptr<WorkerTrace> trace) : trace_(std::move(trace))
    {
        output_ = json{{"ready", !trace_->unavailable}, {"protocol", 1}}.dump() + "\n";
    }
    ~FakeWorker() override { close(); }
    [[nodiscard]] bool has_exited() const noexcept override { return trace_->closed; }
    [[nodiscard]] bool write(std::string_view bytes, std::stop_token cancellation) noexcept override
    {
        if (trace_->beforeWrite) trace_->beforeWrite();
        if (cancellation.stop_requested()) return false;
        try
        {
            auto request = json::parse(bytes);
            auto const index = trace_->requests.size();
            trace_->requests.push_back(request);
            auto const texts = index < trace_->replies.size() ? trace_->replies[index]
                : std::vector<std::string>(request.at("images").size(), "+7");
            output_ = trace_->malformed ? "bad-json\n"
                : json{{"id", request.at("id")}, {"texts", texts}}.dump() + "\n";
            return true;
        }
        catch (...) { return false; }
    }
    [[nodiscard]] Output read(std::chrono::milliseconds timeout, std::stop_token) noexcept override
    {
        if (trace_->stall && !trace_->requests.empty())
        {
            std::this_thread::sleep_for(timeout);
            return {};
        }
        // Exercise streaming rather than assuming one pipe read per JSON line.
        auto const count = (std::min)(std::size_t{11}, output_.size());
        Output result;
        result.standard_output = output_.substr(0, count);
        output_.erase(0, count);
        return result;
    }
    void close() noexcept override { trace_->closed = true; }

private:
    std::shared_ptr<WorkerTrace> trace_;
    std::string output_;
};

class UnusedCli final : public IWin32Process
{
public:
    int calls{};
    [[nodiscard]] Result execute(std::filesystem::path const&, std::wstring const&,
        std::stop_token, std::chrono::milliseconds, std::size_t, bool) override
    {
        ++calls;
        return {};
    }
};

[[nodiscard]] UmaAssistant::Vision::GrayImage gain_crop()
{
    UmaAssistant::Vision::GrayImage crop;
    crop.width = 20;
    crop.height = 10;
    crop.gray.assign(200, 255);
    for (int y = 2; y < 8; ++y)
        for (int x : {3, 8}) crop.gray[static_cast<std::size_t>(y * 20 + x)] = 0;
    return crop;
}

[[nodiscard]] std::vector<NumericOcrBatchField> gain_fields()
{
    return {{"gain", gain_crop(), 999, true}};
}

} // namespace

TEST_CASE("Numeric OCR worker reuses one engine across batches without CLI images", "[OcrWorker]")
{
    UnusedCli process;
    auto trace = std::make_shared<WorkerTrace>();
    int starts = 0;
    NumericOcrBatch service(process, L"tesseract.exe", [&] {
        ++starts;
        return std::make_unique<FakeWorker>(trace);
    });
    auto const fields = gain_fields();
    CHECK(service.read(fields).at("gain").value == 7);
    CHECK(service.read(fields).at("gain").value == 7);
    CHECK(starts == 1);
    CHECK(process.calls == 0);
    REQUIRE(trace->requests.size() == 2);
    auto const& image = trace->requests[0].at("images").at(0);
    CHECK(image.at("width") == 40);
    CHECK(image.at("height") == 20);
    CHECK(image.at("channels") == 1);
    CHECK(image.at("pixels").get<std::string>().size() == 1600);
    CHECK_FALSE(trace->closed);
}

TEST_CASE("Numeric OCR worker preserves glyph validation and unresolved field identity", "[OcrWorker]")
{
    UnusedCli process;
    auto trace = std::make_shared<WorkerTrace>();
    trace->replies = {{"+11", "4%"}, {"+7"}};
    NumericOcrBatch service(process, L"tesseract.exe", [trace] { return std::make_unique<FakeWorker>(trace); });
    auto fields = gain_fields();
    fields.push_back({"failure", gain_crop(), 100, false});
    auto const result = service.read(fields);
    CHECK(result.at("gain").value == 7);
    CHECK(result.at("gain").glyphMismatch);
    CHECK(result.at("failure").value == 4);
    REQUIRE(trace->requests.size() == 2);
    CHECK(trace->requests[0].at("psm") == 7);
    CHECK(trace->requests[1].at("psm") == 8);
    CHECK(trace->requests[1].at("images").size() == 1);
    CHECK(process.calls == 0);
}

TEST_CASE("Numeric OCR worker preserves RGB pixels for unsigned countdown fields", "[OcrWorker]")
{
    UnusedCli process;
    auto trace = std::make_shared<WorkerTrace>();
    trace->replies = {{"11"}};
    NumericOcrBatch service(process, L"tesseract.exe", [trace] { return std::make_unique<FakeWorker>(trace); });
    auto crop = gain_crop();
    crop.rgba.assign(crop.gray.size() * 4u, 255);
    crop.rgba[0] = 10; crop.rgba[1] = 20; crop.rgba[2] = 30;
    std::vector<NumericOcrBatchField> fields{{"turns", std::move(crop), 100, false, NumericOcrFormat::UnsignedInteger}};
    CHECK(service.read(fields).at("turns").value == 11);
    REQUIRE(trace->requests.size() == 1);
    auto const& image = trace->requests[0].at("images").at(0);
    CHECK(image.at("channels") == 3);
    auto const pixels = NumericOcrProtocol::decode_pixels(image.at("pixels").get<std::string>(), 40u * 20u * 3u);
    CHECK(pixels[0] == 10);
    CHECK(pixels[1] == 20);
    CHECK(pixels[2] == 30);
    CHECK(process.calls == 0);
}

TEST_CASE("Numeric OCR worker rejects malformed or missing replies and restarts next batch", "[OcrWorker]")
{
    for (bool const malformed : {false, true})
    {
        CAPTURE(malformed);
        UnusedCli process;
        auto bad = std::make_shared<WorkerTrace>();
        bad->malformed = malformed;
        bad->replies = {{}};
        auto good = std::make_shared<WorkerTrace>();
        int starts = 0;
        NumericOcrBatch service(process, L"tesseract.exe", [&] {
            return std::make_unique<FakeWorker>(starts++ == 0 ? bad : good);
        });
        auto const fields = gain_fields();
        auto const failed = service.read(fields);
        CHECK_FALSE(failed.at("gain").value);
        CHECK_FALSE(failed.at("gain").failureReason.empty());
        CHECK(bad->closed);
        CHECK(service.read(fields).at("gain").value == 7);
        CHECK(starts == 2);
        CHECK(process.calls == 0);
    }
}

TEST_CASE("Numeric OCR worker falls back to CLI only when initialization is unavailable", "[OcrWorker]")
{
    UnusedCli process;
    auto trace = std::make_shared<WorkerTrace>();
    trace->unavailable = true;
    int starts = 0;
    NumericOcrBatch service(process, L"tesseract.exe", [&] {
        ++starts;
        return std::make_unique<FakeWorker>(trace);
    });
    auto const fields = gain_fields();
    auto const first = service.read(fields);
    INFO(first.at("gain").failureReason);
    CHECK_FALSE(first.at("gain").value);
    CHECK_FALSE(service.read(fields).at("gain").value);
    CHECK(starts == 1);
    CHECK(process.calls == 8);
    CHECK(trace->closed);
}

TEST_CASE("Numeric OCR worker cancellation kills the session and propagates cancellation", "[OcrWorker]")
{
    UnusedCli process;
    auto trace = std::make_shared<WorkerTrace>();
    std::stop_source cancellation;
    trace->beforeWrite = [&] { cancellation.request_stop(); };
    NumericOcrBatch service(process, L"tesseract.exe", [trace] { return std::make_unique<FakeWorker>(trace); });
    auto const fields = gain_fields();
    try
    {
        (void)service.read(fields, cancellation.get_token());
        FAIL("Cancellation should propagate.");
    }
    catch (std::system_error const& exception)
    {
        CHECK(exception.code() == std::make_error_code(std::errc::operation_canceled));
    }
    CHECK(trace->closed);
    CHECK(process.calls == 0);
}

TEST_CASE("Numeric OCR worker retains a five second frame budget", "[OcrWorker]")
{
    UnusedCli process;
    auto trace = std::make_shared<WorkerTrace>();
    trace->stall = true;
    NumericOcrBatch service(process, L"tesseract.exe", [trace] { return std::make_unique<FakeWorker>(trace); });
    auto const started = std::chrono::steady_clock::now();
    auto const result = service.read(gain_fields());
    auto const elapsed = std::chrono::steady_clock::now() - started;
    CHECK_FALSE(result.at("gain").value);
    CHECK_FALSE(result.at("gain").failureReason.empty());
    CHECK(elapsed >= std::chrono::seconds(4));
    CHECK(elapsed < std::chrono::seconds(6));
    CHECK(trace->closed);
    CHECK(process.calls == 0);
}

TEST_CASE("Installed Tesseract worker recognizes repeated in-memory requests", "[OcrWorker][OcrIntegration]")
{
    auto const executable = NumericOcrBatch::locate_executable();
    if (executable.empty()) SKIP("Tesseract is not installed.");
    // Blank images exercise the real model/worker lifecycle without introducing
    // a machine-font dependency into recognition correctness tests.
    UnusedCli process;
    int starts = 0;
    NumericOcrBatch service(process, executable, [&] {
        ++starts;
        std::string error;
        return UmaAssistant::start_win32_interactive_session(
            std::filesystem::path(UMA_NUMERIC_OCR_WORKER_PATH),
            {UmaAssistant::Paths::ToUtf8(executable)}, error);
    });
    auto fields = gain_fields();
    fields[0].crop.gray.assign(200, 255);
    fields[0].validateGainGlyphs = false;
    for (int iteration = 0; iteration < 2; ++iteration)
    {
        auto const result = service.read(fields);
        CHECK_FALSE(result.at("gain").value);
        CHECK(result.at("gain").failureReason.empty());
    }
    CHECK(starts == 1);
    CHECK(process.calls == 0);
}

TEST_CASE("Reused Tesseract engine preserves real training gain readings", "[OcrWorker][OcrIntegration]")
{
    auto const executable = NumericOcrBatch::locate_executable();
    if (executable.empty()) SKIP("Tesseract is not installed.");
    auto process = UmaAssistant::create_win32_process();
    int starts = 0;
    NumericOcrBatch worker(*process, executable, [&] {
        ++starts;
        std::string error;
        return UmaAssistant::start_win32_interactive_session(
            std::filesystem::path(UMA_NUMERIC_OCR_WORKER_PATH),
            {UmaAssistant::Paths::ToUtf8(executable)}, error);
    });
    NumericOcrBatch legacy(*process, executable);
    UmaColorRule rule{};
    rule.minimum_red = 240; rule.minimum_green = 95; rule.minimum_blue = 0;
    rule.maximum_red = 255; rule.maximum_green = 225; rule.maximum_blue = 95;
    rule.minimum_red_green = 25; rule.maximum_red_green = 255;
    rule.minimum_green_blue = -255; rule.maximum_green_blue = 255;
    rule.sample_step = 2;
    struct Fixture { char const* name; std::array<int, 6> values; };
    std::array const fixtures{
        Fixture{"speed-1", {19, 0, 9, 0, 0, 6}},
        Fixture{"stamina-1", {0, 13, 0, 7, 0, 4}},
        Fixture{"power-1", {0, 10, 13, 0, 0, 5}},
        Fixture{"guts-1", {12, 0, 11, 15, 0, 7}},
        Fixture{"wit-1", {4, 0, 0, 0, 17, 8}},
        Fixture{"wit-speed-five", {5, 0, 0, 0, 16, 8}},
    };
    std::array const lefts{25, 177, 317, 457, 597, 746};
    std::array const widths{152, 140, 140, 140, 141, 127};
    std::chrono::steady_clock::duration worker_time{};
    std::chrono::steady_clock::duration cli_time{};
    for (auto const& fixture : fixtures)
    {
        CAPTURE(fixture.name);
        auto const image = UmaAssistant::Resources::WicImageDecoder::decode_file(
            std::filesystem::path(CMAKE_SOURCE_DIR) / "testdata/hachimi/ura/captures/smart_runtime_20261006"
                / (std::string(fixture.name) + ".png"));
        REQUIRE(image.width == 900);
        REQUIRE(image.height == 1600);
        std::vector<NumericOcrBatchField> fields;
        for (std::size_t column = 0; column < fixture.values.size(); ++column)
        {
            if (fixture.values[column] == 0) continue;
            UmaAssistant::Vision::GrayImage crop;
            crop.width = widths[column]; crop.height = 65;
            for (int y = 965; y < 1030; ++y)
                for (int x = lefts[column]; x < lefts[column] + crop.width; ++x)
                {
                    auto const at = static_cast<std::size_t>(y * image.width + x);
                    crop.gray.push_back(image.gray[at]);
                    crop.rgba.insert(crop.rgba.end(), image.rgba.begin() + static_cast<std::ptrdiff_t>(at * 4u),
                        image.rgba.begin() + static_cast<std::ptrdiff_t>(at * 4u + 4u));
                }
            auto prepared = UmaAssistant::Vision::prepare_numeric_glyphs(crop, true, rule);
            REQUIRE(prepared.has_value());
            fields.push_back({std::to_string(column), std::move(*prepared), 999, true});
        }
        auto started = std::chrono::steady_clock::now();
        auto const reused = worker.read(fields);
        worker_time += std::chrono::steady_clock::now() - started;
        started = std::chrono::steady_clock::now();
        auto const original = legacy.read(fields);
        cli_time += std::chrono::steady_clock::now() - started;
        for (auto const& field : fields)
        {
            CAPTURE(field.key, reused.at(field.key).rawText, original.at(field.key).rawText);
            CHECK(reused.at(field.key).value == original.at(field.key).value);
            CHECK(reused.at(field.key).value == fixture.values[static_cast<std::size_t>(std::stoi(field.key))]);
        }
    }
    CHECK(starts == 1);
    std::cout << "Six real training gain batches (including worker cold start): worker_ms="
        << std::chrono::duration_cast<std::chrono::milliseconds>(worker_time).count()
        << ", cli_ms=" << std::chrono::duration_cast<std::chrono::milliseconds>(cli_time).count() << '\n';
}

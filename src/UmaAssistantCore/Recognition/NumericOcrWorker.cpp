// Isolating Tesseract allows the caller to enforce a hard frame deadline while
// retaining the language model between requests. No images are written to disk.
#include "NumericOcrProtocol.hpp"
#include "../Common/PathEncoding.hpp"

#include <tesseract/baseapi.h>
#include <tesseract/ocrclass.h>
#include <nlohmann/json.hpp>

#include <array>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>

#if defined(__APPLE__)
#include <mach-o/dyld.h>
#elif defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif

namespace {

using nlohmann::json;
namespace Protocol = UmaAssistant::Recognition::NumericOcrProtocol;

std::filesystem::path locate_tessdata(std::filesystem::path const& hint)
{
    char const* env = std::getenv("TESSDATA_PREFIX");
    if (env && *env)
    {
        std::filesystem::path p(env);
        std::error_code ec;
        if (std::filesystem::is_directory(p, ec) && std::filesystem::exists(p / "eng.traineddata", ec))
            return p;
        if (std::filesystem::is_directory(p / "tessdata", ec) && std::filesystem::exists(p / "tessdata" / "eng.traineddata", ec))
            return p / "tessdata";
    }

    std::vector<std::filesystem::path> candidates;

    if (!hint.empty())
    {
        auto const dir = std::filesystem::absolute(hint).parent_path();
        candidates.push_back(dir / "tessdata");
        candidates.push_back(dir / "resource" / "tessdata");
        candidates.push_back(hint);
    }

#if defined(__APPLE__)
    char exe_buf[1024];
    uint32_t size = sizeof(exe_buf);
    if (_NSGetExecutablePath(exe_buf, &size) == 0)
    {
        std::filesystem::path exe_dir = std::filesystem::path(exe_buf).parent_path();
        for (std::filesystem::path cur = exe_dir; !cur.empty(); cur = cur.parent_path())
        {
            candidates.push_back(cur / "resource" / "tessdata");
            candidates.push_back(cur / "tessdata");
            candidates.push_back(cur / "Resources" / "resource" / "tessdata");
            candidates.push_back(cur / "Resources" / "tessdata");
            if (cur == cur.parent_path()) break;
        }
    }
#elif defined(_WIN32)
    std::array<wchar_t, 32768> buffer{};
    auto const len = ::GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (len > 0 && len < buffer.size())
    {
        std::filesystem::path exe_dir = std::filesystem::path(std::wstring(buffer.data(), len)).parent_path();
        for (std::filesystem::path cur = exe_dir; !cur.empty(); cur = cur.parent_path())
        {
            candidates.push_back(cur / "resource" / "tessdata");
            candidates.push_back(cur / "tessdata");
            if (cur == cur.parent_path()) break;
        }
    }
#endif

    for (std::filesystem::path cur = std::filesystem::current_path(); !cur.empty(); cur = cur.parent_path())
    {
        candidates.push_back(cur / "resource" / "tessdata");
        candidates.push_back(cur / "tessdata");
        if (cur == cur.parent_path()) break;
    }

#if defined(CMAKE_SOURCE_DIR)
    candidates.push_back(std::filesystem::path(CMAKE_SOURCE_DIR) / "resource" / "tessdata");
#endif

    for (auto const& candidate : candidates)
    {
        std::error_code ec;
        if (std::filesystem::is_directory(candidate, ec) &&
            std::filesystem::exists(candidate / "eng.traineddata", ec))
        {
            return candidate;
        }
    }

    return std::filesystem::current_path() / "resource" / "tessdata";
}

class TesseractEngine
{
public:
    explicit TesseractEngine(std::filesystem::path const& hint)
    {
#if defined(_WIN32)
        ::SetEnvironmentVariableW(L"OMP_THREAD_LIMIT", L"1");
#else
        setenv("OMP_THREAD_LIMIT", "1", 1);
#endif
        auto const data_path = locate_tessdata(hint);
        if (api_.Init(data_path.string().c_str(), "eng") != 0)
        {
            throw std::runtime_error("Could not load the Tesseract English model from " + data_path.string());
        }
        api_.SetVariable("classify_enable_learning", "0");
#if defined(_WIN32)
        api_.SetVariable("debug_file", "NUL");
#else
        api_.SetVariable("debug_file", "/dev/null");
#endif
    }

    ~TesseractEngine()
    {
        api_.End();
    }

    TesseractEngine(TesseractEngine const&) = delete;
    TesseractEngine& operator=(TesseractEngine const&) = delete;

    [[nodiscard]] std::string read(json const& image, int mode)
    {
        int const width = image.at("width").get<int>();
        int const height = image.at("height").get<int>();
        int const channels = image.at("channels").get<int>();
        if (width <= 0 || height <= 0 || width > 10000 || height > 10000
            || (channels != 1 && channels != 3))
            throw std::runtime_error("Invalid numeric OCR image dimensions.");

        auto const size = static_cast<std::size_t>(width) * static_cast<std::size_t>(height)
            * static_cast<std::size_t>(channels);
        auto const pixels = Protocol::decode_pixels(image.at("pixels").get_ref<std::string const&>(), size);

        api_.Clear();
        api_.ClearAdaptiveClassifier();
        api_.SetPageSegMode(static_cast<tesseract::PageSegMode>(mode));
        api_.SetImage(pixels.data(), width, height, channels, width * channels);
        if (api_.Recognize(nullptr) != 0)
            return {};

        std::unique_ptr<char[]> text(api_.GetUTF8Text());
        if (!text) return {};
        return text.get();
    }

private:
    tesseract::TessBaseAPI api_;
};

int run_worker(std::filesystem::path const& hint)
{
    try
    {
        TesseractEngine engine{hint};
        std::cout << json{{"ready", true}, {"protocol", 1}}.dump() << '\n' << std::flush;
        std::string line;
        while (std::getline(std::cin, line))
        {
            if (line.size() > Protocol::max_request_bytes) return 3;
            auto const request = json::parse(line);
            int const mode = request.at("psm").get<int>();
            if (mode != 7 && mode != 8 && mode != 13) return 3;
            auto const& images = request.at("images");
            if (!images.is_array() || images.empty() || images.size() > 64) return 3;
            json texts = json::array();
            for (auto const& image : images) texts.push_back(engine.read(image, mode));
            auto const response = json{{"id", request.at("id")}, {"texts", std::move(texts)}}.dump();
            if (response.size() > Protocol::max_response_bytes) return 3;
            std::cout << response << '\n' << std::flush;
        }
        return 0;
    }
    catch (std::exception const& exception)
    {
        std::cerr << exception.what() << '\n';
        return 1;
    }
}

} // namespace

#if defined(_WIN32) && !defined(__MINGW32__)
int wmain(int argc, wchar_t* argv[])
{
    std::filesystem::path hint;
    if (argc >= 2 && argv[1])
    {
        hint = argv[1];
    }
    return run_worker(hint);
}
#else
int main(int argc, char* argv[])
{
    std::filesystem::path hint;
    if (argc >= 2 && argv[1])
    {
        hint = argv[1];
    }
    return run_worker(hint);
}
#endif

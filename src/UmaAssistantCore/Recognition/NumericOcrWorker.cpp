// Isolating Tesseract allows the caller to enforce a hard frame deadline while
// retaining the language model between requests. No images are written to disk.
#include "NumericOcrProtocol.hpp"
#include "../Common/PathEncoding.hpp"

#include <Windows.h>
#include <nlohmann/json.hpp>

#include <array>
#include <cstring>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <string>

namespace {

using nlohmann::json;
namespace Protocol = UmaAssistant::Recognition::NumericOcrProtocol;

class TesseractEngine
{
public:
    explicit TesseractEngine(std::filesystem::path const& executable)
    {
        // This setting belongs only to this isolated worker, never the GUI.
        ::SetEnvironmentVariableW(L"OMP_THREAD_LIMIT", L"1");
        auto const directory = std::filesystem::absolute(executable).parent_path();
        for (auto const* name : {L"libtesseract-5.dll", L"tesseract55.dll", L"tesseract54.dll",
                L"tesseract53.dll", L"tesseract50.dll", L"tesseract.dll"})
        {
            library_ = ::LoadLibraryExW((directory / name).c_str(), nullptr,
                LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
            if (library_) break;
        }
        if (!library_) throw std::runtime_error("The installed Tesseract C API is unavailable.");
        try
        {
            auto const create = bind<void* (*)()>("TessBaseAPICreate");
            destroy_ = bind<void (*)(void*)>("TessBaseAPIDelete");
            auto const initialize = bind<int (*)(void*, char const*, char const*)>("TessBaseAPIInit3");
            set_mode_ = bind<void (*)(void*, int)>("TessBaseAPISetPageSegMode");
            set_image_ = bind<void (*)(void*, unsigned char const*, int, int, int, int)>("TessBaseAPISetImage");
            recognize_ = bind<int (*)(void*, void*)>("TessBaseAPIRecognize");
            get_text_ = bind<char* (*)(void*)>("TessBaseAPIGetUTF8Text");
            delete_text_ = bind<void (*)(char const*)>("TessDeleteText");
            clear_ = bind<void (*)(void*)>("TessBaseAPIClear");
            clear_adaptive_ = bind<void (*)(void*)>("TessBaseAPIClearAdaptiveClassifier");
            auto const set_variable = bind<int (*)(void*, char const*, char const*)>("TessBaseAPISetVariable");
            handle_ = create();
            if (!handle_) throw std::runtime_error("Could not create the Tesseract engine.");
            std::array<wchar_t, 32768> prefix{};
            auto const length = ::GetEnvironmentVariableW(L"TESSDATA_PREFIX", prefix.data(),
                static_cast<DWORD>(prefix.size()));
            auto const data = length > 0 && length < prefix.size()
                ? std::filesystem::path(std::wstring(prefix.data(), length)) : directory / L"tessdata";
            auto const path = UmaAssistant::Paths::ToUtf8(data);
            if (initialize(handle_, path.c_str(), "eng") != 0)
                throw std::runtime_error("Could not load the Tesseract English model.");
            (void)set_variable(handle_, "classify_enable_learning", "0");
            (void)set_variable(handle_, "debug_file", "NUL");
        }
        catch (...)
        {
            release();
            throw;
        }
    }

    ~TesseractEngine() { release(); }
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
        clear_(handle_);
        clear_adaptive_(handle_);
        set_mode_(handle_, mode);
        set_image_(handle_, pixels.data(), width, height, channels, width * channels);
        if (recognize_(handle_, nullptr) != 0)
            return {};
        auto* text = get_text_(handle_);
        if (!text) return {};
        struct TextGuard { char* text; void (*free)(char const*); ~TextGuard() { free(text); } } guard{text, delete_text_};
        return text;
    }

private:
    template<class Function> [[nodiscard]] Function bind(char const* name)
    {
        auto const address = ::GetProcAddress(library_, name);
        if (!address) throw std::runtime_error("The installed Tesseract C API is incomplete.");
        Function result{};
        static_assert(sizeof(result) == sizeof(address));
        std::memcpy(&result, &address, sizeof(result));
        return result;
    }

    void release() noexcept
    {
        if (handle_ && destroy_) destroy_(handle_);
        handle_ = nullptr;
        if (library_) ::FreeLibrary(library_);
        library_ = nullptr;
    }

    HMODULE library_{};
    void* handle_{};
    void (*destroy_)(void*){};
    void (*set_mode_)(void*, int){};
    void (*set_image_)(void*, unsigned char const*, int, int, int, int){};
    int (*recognize_)(void*, void*){};
    char* (*get_text_)(void*){};
    void (*delete_text_)(char const*){};
    void (*clear_)(void*){};
    void (*clear_adaptive_)(void*){};
};

} // namespace

int wmain(int argc, wchar_t* argv[])
{
    try
    {
        if (argc != 2) return 2;
        TesseractEngine engine{std::filesystem::path(argv[1])};
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

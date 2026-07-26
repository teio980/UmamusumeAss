#include "Utf8Path.hpp"

#include <limits>
#include <string>

#if defined(_WIN32)
#  include <windows.h>
#endif

namespace UmaAssistant {

std::optional<std::filesystem::path> path_from_utf8(std::string_view value) noexcept
{
    try
    {
#if defined(_WIN32)
        if (value.size() > static_cast<std::size_t>((std::numeric_limits<int>::max)()))
        {
            return std::nullopt;
        }
        auto const input_size = static_cast<int>(value.size());
        auto const output_size = MultiByteToWideChar(
            CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), input_size, nullptr, 0);
        if (output_size == 0) return std::nullopt;

        std::wstring decoded(static_cast<std::size_t>(output_size), L'\0');
        if (MultiByteToWideChar(
                CP_UTF8,
                MB_ERR_INVALID_CHARS,
                value.data(),
                input_size,
                decoded.data(),
                output_size) != output_size)
        {
            return std::nullopt;
        }
        return std::filesystem::path(std::move(decoded));
#else
        return std::filesystem::path(std::string(value));
#endif
    }
    catch (...)
    {
        return std::nullopt;
    }
}

}

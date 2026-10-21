#include "WicImageDecoder.hpp"

#include <Windows.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <limits>
#include <stdexcept>
#include <system_error>

namespace UmaAssistant::Resources::WicImageDecoder {
namespace {

using Microsoft::WRL::ComPtr;
constexpr std::uint32_t max_dimension = 10000;
constexpr std::uint64_t max_pixels = 100'000'000;

class ComScope
{
public:
    ComScope()
    {
        auto const result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (SUCCEEDED(result)) initialized_ = true;
        else if (result != RPC_E_CHANGED_MODE)
            throw std::system_error(result, std::system_category(), "CoInitializeEx");
    }

    ~ComScope()
    {
        if (initialized_) CoUninitialize();
    }

    ComScope(ComScope const&) = delete;
    ComScope& operator=(ComScope const&) = delete;

private:
    bool initialized_{};
};

void check_hresult(HRESULT result, char const* operation)
{
    if (FAILED(result)) throw std::system_error(result, std::system_category(), operation);
}

[[nodiscard]] std::uint64_t checked_pixel_count(std::uint32_t width, std::uint32_t height)
{
    auto const pixel_count = static_cast<std::uint64_t>(width) * height;
    if (width == 0 || height == 0 || width > max_dimension || height > max_dimension
        || pixel_count > max_pixels)
        throw std::runtime_error("WIC image dimensions exceed supported limits");
    if (pixel_count * 4u > (std::numeric_limits<UINT>::max)())
        throw std::runtime_error("WIC image byte length exceeds UINT");
    return pixel_count;
}

[[nodiscard]] Vision::GrayImage make_rgba_image(
    std::uint32_t width,
    std::uint32_t height,
    std::span<std::uint8_t const> rgba)
{
    auto const pixel_count = checked_pixel_count(width, height);
    auto const rgba_size = pixel_count * 4u;
    if (rgba.size() != rgba_size)
        throw std::invalid_argument("RGBA image byte length does not match its dimensions");

    Vision::GrayImage image;
    image.width = static_cast<std::int32_t>(width);
    image.height = static_cast<std::int32_t>(height);
    image.rgba.assign(rgba.begin(), rgba.end());
    image.gray.resize(static_cast<std::size_t>(pixel_count));
    for (std::size_t index = 0; index < image.gray.size(); ++index)
    {
        auto const offset = index * 4;
        image.gray[index] = static_cast<std::uint8_t>(
            (static_cast<std::uint32_t>(image.rgba[offset]) * 299u
                + static_cast<std::uint32_t>(image.rgba[offset + 1]) * 587u
                + static_cast<std::uint32_t>(image.rgba[offset + 2]) * 114u) / 1000u);
    }
    return image;
}

[[nodiscard]] Vision::GrayImage decode_frame(IWICImagingFactory* factory, IWICBitmapFrameDecode* frame)
{
    UINT width{};
    UINT height{};
    check_hresult(frame->GetSize(&width, &height), "IWICBitmapSource::GetSize");
    auto const pixel_count = checked_pixel_count(width, height);

    ComPtr<IWICFormatConverter> converter;
    check_hresult(factory->CreateFormatConverter(&converter), "CreateFormatConverter");
    check_hresult(converter->Initialize(frame, GUID_WICPixelFormat32bppRGBA,
        WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom),
        "IWICFormatConverter::Initialize");

    Vision::GrayImage image;
    image.width = static_cast<std::int32_t>(width);
    image.height = static_cast<std::int32_t>(height);
    image.rgba.resize(static_cast<std::size_t>(pixel_count * 4u));
    check_hresult(converter->CopyPixels(nullptr, width * 4u,
        static_cast<UINT>(image.rgba.size()), image.rgba.data()), "IWICBitmapSource::CopyPixels");
    image.gray.resize(static_cast<std::size_t>(pixel_count));
    for (std::size_t index = 0; index < image.gray.size(); ++index)
    {
        auto const offset = index * 4;
        image.gray[index] = static_cast<std::uint8_t>(
            (static_cast<std::uint32_t>(image.rgba[offset]) * 299u
                + static_cast<std::uint32_t>(image.rgba[offset + 1]) * 587u
                + static_cast<std::uint32_t>(image.rgba[offset + 2]) * 114u) / 1000u);
    }
    return image;
}

[[nodiscard]] ComPtr<IWICImagingFactory> create_factory()
{
    ComPtr<IWICImagingFactory> factory;
    check_hresult(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&factory)), "CoCreateInstance(WIC factory)");
    return factory;
}

[[nodiscard]] Vision::GrayImage decode_stream(IWICImagingFactory* factory, IStream* stream)
{
    ComPtr<IWICBitmapDecoder> decoder;
    check_hresult(factory->CreateDecoderFromStream(stream, nullptr,
        WICDecodeMetadataCacheOnLoad, &decoder), "CreateDecoderFromStream");
    ComPtr<IWICBitmapFrameDecode> frame;
    check_hresult(decoder->GetFrame(0, &frame), "IWICBitmapDecoder::GetFrame");
    return decode_frame(factory, frame.Get());
}

[[nodiscard]] std::filesystem::path path_from_utf8(std::string_view value)
{
    std::u8string utf8;
    utf8.reserve(value.size());
    for (char const byte : value)
        utf8.push_back(static_cast<char8_t>(static_cast<unsigned char>(byte)));
    return std::filesystem::path(utf8);
}

}

Vision::GrayImage decode_file(std::filesystem::path const& path)
{
    ComScope apartment;
    auto factory = create_factory();
    ComPtr<IWICBitmapDecoder> decoder;
    check_hresult(factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ,
        WICDecodeMetadataCacheOnLoad, &decoder), "CreateDecoderFromFilename");
    ComPtr<IWICBitmapFrameDecode> frame;
    check_hresult(decoder->GetFrame(0, &frame), "IWICBitmapDecoder::GetFrame");
    return decode_frame(factory.Get(), frame.Get());
}

Vision::GrayImage decode_file_utf8(std::string_view path)
{
    if (path.empty()) throw std::invalid_argument("Image path cannot be empty");
    return decode_file(path_from_utf8(path));
}

Vision::GrayImage decode_bytes(std::span<std::uint8_t const> encoded)
{
    if (encoded.empty() || encoded.size() > (std::numeric_limits<DWORD>::max)())
        throw std::invalid_argument("Encoded image byte length is outside supported limits");

    ComScope apartment;
    auto factory = create_factory();
    ComPtr<IWICStream> stream;
    check_hresult(factory->CreateStream(&stream), "CreateStream");
    check_hresult(stream->InitializeFromMemory(
        const_cast<BYTE*>(encoded.data()), static_cast<DWORD>(encoded.size())),
        "IWICStream::InitializeFromMemory");
    return decode_stream(factory.Get(), stream.Get());
}

Vision::GrayImage from_rgba(
    std::int32_t width,
    std::int32_t height,
    std::span<std::uint8_t const> rgba)
{
    if (width <= 0 || height <= 0)
        throw std::invalid_argument("RGBA image dimensions must be positive");
    return make_rgba_image(static_cast<std::uint32_t>(width),
        static_cast<std::uint32_t>(height), rgba);
}

}

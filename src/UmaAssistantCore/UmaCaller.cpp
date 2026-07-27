#include "UmaAssistant/UmaCaller.h"

#include "CoreHandle.hpp"
#include "CoreRuntime.hpp"

#include <string>

namespace {

[[nodiscard]] UmaStartResult unsupported_start() noexcept
{
    return {0, UMA_ERROR_INVALID_ARGUMENT};
}

}

extern "C" {

UMA_API_PORT char const* UMA_CALL UmaGetVersion(void)
{
    try
    {
        return "0.1.0";
    }
    catch (...)
    {
        return "";
    }
}

UmaHandle UMA_API UmaCreate(UmaApiCallback callback, void* custom_arg)
{
    try
    {
        return UmaAssistant::CoreApi::create_handle(callback, custom_arg);
    }
    catch (...)
    {
        return nullptr;
    }
}

void UMA_API UmaDestroy(UmaHandle handle)
{
    try
    {
        UmaAssistant::CoreApi::destroy_handle(handle);
    }
    catch (...)
    {
        return;
    }
}

int32_t UMA_API UmaSetUserDir(char const* utf8_path)
{
    try
    {
        if (utf8_path == nullptr || *utf8_path == '\0') return UMA_ERROR_INVALID_ARGUMENT;
        return UmaAssistant::CoreRuntime::instance().set_user_dir(utf8_path)
            ? UMA_SUCCESS
            : UMA_ERROR_INVALID_ARGUMENT;
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

int32_t UMA_API UmaLoadResource(char const* utf8_path)
{
    try
    {
        if (utf8_path == nullptr || *utf8_path == '\0') return UMA_ERROR_INVALID_ARGUMENT;
        return UmaAssistant::CoreRuntime::instance().load_resource(utf8_path)
            ? UMA_SUCCESS
            : UMA_ERROR_INVALID_ARGUMENT;
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

UmaStartResult UMA_API UmaConnectAsync(
    UmaHandle handle, char const* adb_path, char const* serial, char const* profile)
{
    try
    {
        if (adb_path == nullptr || serial == nullptr || profile == nullptr)
        {
            return unsupported_start();
        }
        return UmaAssistant::CoreApi::start_connect(
            handle, std::string(adb_path), std::string(serial), std::string(profile));
    }
    catch (...)
    {
        return unsupported_start();
    }
}

int32_t UMA_API UmaCancelConnect(UmaHandle handle, uint64_t operation_id)
{
    try
    {
        return UmaAssistant::CoreApi::cancel_operation(handle, operation_id);
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

int32_t UMA_API UmaCancelOperation(UmaHandle handle, uint64_t operation_id)
{
    try
    {
        return UmaAssistant::CoreApi::cancel_operation(handle, operation_id);
    }
    catch (...)
    {
        return UMA_ERROR_INVALID_ARGUMENT;
    }
}

UmaStartResult UMA_API UmaVerifyGameAsync(UmaHandle, char const*)
{
    try { return unsupported_start(); } catch (...) { return unsupported_start(); }
}

UmaStartResult UMA_API UmaCaptureAsync(UmaHandle)
{
    try { return unsupported_start(); } catch (...) { return unsupported_start(); }
}

int32_t UMA_API UmaGetFramePngSize(UmaHandle, uint64_t, uint64_t*)
{
    try { return UMA_ERROR_INVALID_ARGUMENT; } catch (...) { return UMA_ERROR_INVALID_ARGUMENT; }
}

int32_t UMA_API UmaCopyFramePng(UmaHandle, uint64_t, uint8_t*, uint64_t)
{
    try { return UMA_ERROR_INVALID_ARGUMENT; } catch (...) { return UMA_ERROR_INVALID_ARGUMENT; }
}

int32_t UMA_API UmaReleaseFrame(UmaHandle, uint64_t)
{
    try { return UMA_ERROR_INVALID_ARGUMENT; } catch (...) { return UMA_ERROR_INVALID_ARGUMENT; }
}

UmaStartResult UMA_API UmaTapAsync(UmaHandle, uint64_t, int32_t, int32_t)
{
    try { return unsupported_start(); } catch (...) { return unsupported_start(); }
}

UmaStartResult UMA_API UmaSwipeAsync(
    UmaHandle, uint64_t, int32_t, int32_t, int32_t, int32_t, int32_t)
{
    try { return unsupported_start(); } catch (...) { return unsupported_start(); }
}

}

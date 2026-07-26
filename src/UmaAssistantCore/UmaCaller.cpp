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

UMA_API char const* UMA_CALL UmaGetVersion(void)
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

UMA_API UmaHandle UMA_CALL UmaCreate(UmaApiCallback callback, void* custom_arg)
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

UMA_API void UMA_CALL UmaDestroy(UmaHandle handle)
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

UMA_API int32_t UMA_CALL UmaSetUserDir(char const* utf8_path)
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

UMA_API int32_t UMA_CALL UmaLoadResource(char const* utf8_path)
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

UMA_API UmaStartResult UMA_CALL UmaConnectAsync(
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

UMA_API int32_t UMA_CALL UmaCancelConnect(UmaHandle handle, uint64_t operation_id)
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

UMA_API int32_t UMA_CALL UmaCancelOperation(UmaHandle handle, uint64_t operation_id)
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

UMA_API UmaStartResult UMA_CALL UmaVerifyGameAsync(UmaHandle, char const*)
{
    try { return unsupported_start(); } catch (...) { return unsupported_start(); }
}

UMA_API UmaStartResult UMA_CALL UmaCaptureAsync(UmaHandle)
{
    try { return unsupported_start(); } catch (...) { return unsupported_start(); }
}

UMA_API int32_t UMA_CALL UmaGetFramePngSize(UmaHandle, uint64_t, uint64_t*)
{
    try { return UMA_ERROR_INVALID_ARGUMENT; } catch (...) { return UMA_ERROR_INVALID_ARGUMENT; }
}

UMA_API int32_t UMA_CALL UmaCopyFramePng(UmaHandle, uint64_t, uint8_t*, uint64_t)
{
    try { return UMA_ERROR_INVALID_ARGUMENT; } catch (...) { return UMA_ERROR_INVALID_ARGUMENT; }
}

UMA_API int32_t UMA_CALL UmaReleaseFrame(UmaHandle, uint64_t)
{
    try { return UMA_ERROR_INVALID_ARGUMENT; } catch (...) { return UMA_ERROR_INVALID_ARGUMENT; }
}

UMA_API UmaStartResult UMA_CALL UmaTapAsync(UmaHandle, uint64_t, int32_t, int32_t)
{
    try { return unsupported_start(); } catch (...) { return unsupported_start(); }
}

UMA_API UmaStartResult UMA_CALL UmaSwipeAsync(
    UmaHandle, uint64_t, int32_t, int32_t, int32_t, int32_t, int32_t)
{
    try { return unsupported_start(); } catch (...) { return unsupported_start(); }
}

}

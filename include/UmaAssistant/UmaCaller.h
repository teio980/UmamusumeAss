#pragma once

#include <stdint.h>

#if defined(_WIN32)
#  define UMA_CALL __stdcall
#  if defined(UMA_DLL_EXPORTS)
#    define UMA_API __declspec(dllexport)
#  else
#    define UMA_API __declspec(dllimport)
#  endif
#else
#  define UMA_CALL
#  define UMA_API
#endif

typedef struct UmaHandleImpl* UmaHandle;

typedef struct UmaStartResult
{
    uint64_t operation_id;
    int32_t error_code;
} UmaStartResult;

typedef void (UMA_CALL* UmaApiCallback)(
    int32_t message,
    char const* details_json,
    void* custom_arg);

#define UMA_MSG_CONNECTION_STARTED 1
#define UMA_MSG_CONNECTION_PROGRESS 2
#define UMA_MSG_CONNECTION_SUCCEEDED 3
#define UMA_MSG_CONNECTION_FAILED 4
#define UMA_MSG_GAME_VERIFIED 5
#define UMA_MSG_GAME_VERIFICATION_FAILED 6
#define UMA_MSG_FRAME_CAPTURED 7
#define UMA_MSG_INPUT_SUCCEEDED 8
#define UMA_MSG_INPUT_FAILED 9
#define UMA_MSG_DEVICE_DISCONNECTED 10

#define UMA_SUCCESS 0
#define UMA_ERROR_ADB_EXECUTABLE_NOT_FOUND 1
#define UMA_ERROR_PROCESS_START_FAILED 2
#define UMA_ERROR_COMMAND_TIMED_OUT 3
#define UMA_ERROR_DEVICE_UNAUTHORIZED 4
#define UMA_ERROR_DEVICE_OFFLINE 5
#define UMA_ERROR_DEVICE_UNAVAILABLE 6
#define UMA_ERROR_COMMAND_FAILED 7
#define UMA_ERROR_INVALID_DEVICE_RESPONSE 8
#define UMA_ERROR_CANCELED 9
#define UMA_ERROR_DEVICE_NOT_READY 10
#define UMA_ERROR_INVALID_ARGUMENT 11
#define UMA_ERROR_BUSY 12
#define UMA_ERROR_BOOT_NOT_COMPLETED 13
#define UMA_ERROR_TARGET_GAME_NOT_INSTALLED 14
#define UMA_ERROR_DEVICE_DISCONNECTED 15

#ifdef __cplusplus
extern "C" {
#endif

UMA_API char const* UMA_CALL UmaGetVersion(void);
UMA_API UmaHandle UMA_CALL UmaCreate(UmaApiCallback callback, void* custom_arg);
UMA_API void UMA_CALL UmaDestroy(UmaHandle handle);
UMA_API int32_t UMA_CALL UmaSetUserDir(char const* utf8_path);
UMA_API int32_t UMA_CALL UmaLoadResource(char const* utf8_path);
UMA_API UmaStartResult UMA_CALL UmaConnectAsync(
    UmaHandle handle, char const* adb_path, char const* serial, char const* profile);
UMA_API int32_t UMA_CALL UmaCancelConnect(UmaHandle handle, uint64_t operation_id);
UMA_API int32_t UMA_CALL UmaCancelOperation(UmaHandle handle, uint64_t operation_id);

UMA_API UmaStartResult UMA_CALL UmaVerifyGameAsync(UmaHandle handle, char const* utf8_package_id);
UMA_API UmaStartResult UMA_CALL UmaCaptureAsync(UmaHandle handle);
UMA_API int32_t UMA_CALL UmaGetFramePngSize(UmaHandle handle, uint64_t frame_id, uint64_t* size);
UMA_API int32_t UMA_CALL UmaCopyFramePng(
    UmaHandle handle, uint64_t frame_id, uint8_t* destination, uint64_t capacity);
UMA_API int32_t UMA_CALL UmaReleaseFrame(UmaHandle handle, uint64_t frame_id);
UMA_API UmaStartResult UMA_CALL UmaTapAsync(
    UmaHandle handle, uint64_t frame_id, int32_t canonical_x, int32_t canonical_y);
UMA_API UmaStartResult UMA_CALL UmaSwipeAsync(
    UmaHandle handle, uint64_t frame_id,
    int32_t x1, int32_t y1, int32_t x2, int32_t y2, int32_t duration_ms);

#ifdef __cplusplus
}
#endif

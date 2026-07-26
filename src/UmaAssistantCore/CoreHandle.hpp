#pragma once

#include "UmaAssistant/UmaCaller.h"

#include <cstdint>
#include <string>

namespace UmaAssistant::CoreApi {

[[nodiscard]] UmaHandle create_handle(UmaApiCallback callback, void* custom_arg);
void destroy_handle(UmaHandle handle) noexcept;
[[nodiscard]] UmaStartResult start_connect(
    UmaHandle handle, std::string adb_path, std::string serial, std::string profile);
[[nodiscard]] std::int32_t cancel_operation(
    UmaHandle handle, std::uint64_t operation_id) noexcept;

}

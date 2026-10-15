#pragma once

#include "Connection/ConnectionProfile.hpp"
#include "Connection/AdbCommandRunner.hpp"
#include "UmaAssistant/Connection.hpp"
#include "Vision/TemplateMatcher.hpp"

#include <cstdint>
#include <chrono>
#include <cstddef>
#include <filesystem>
#include <memory>
#include <mutex>
#include <optional>
#include <stop_token>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

namespace UmaAssistant {

struct SessionFailure
{
    std::int32_t error_code{};
    std::string phase;
    std::string message;
};

struct DeviceFrameMetadata
{
    std::uint64_t id{};
    std::uint64_t session_generation{};
    std::uint64_t geometry_generation{};
    std::int32_t width{};
    std::int32_t height{};
    std::int32_t orientation{};
    std::uint64_t byte_count{};
    double display_scale{};
    double display_offset_x{};
    double display_offset_y{};
};

struct DeviceFrame
{
    DeviceFrameMetadata metadata;
    std::vector<std::uint8_t> png;
    std::shared_ptr<const Vision::GrayImage> pixels;
};

[[nodiscard]] std::pair<int, int> MapImagePointToCanonical(
    int x, int y, DeviceFrameMetadata const& metadata);

struct DeviceOperationResult
{
    struct Point
    {
        std::int32_t canonical_x{};
        std::int32_t canonical_y{};
        std::int32_t device_x{};
        std::int32_t device_y{};
    };
    std::optional<SessionFailure> failure;
    std::optional<DeviceFrameMetadata> frame;
    std::optional<DeviceFrameMetadata> source_frame;
    std::optional<Point> requested_first_point;
    std::optional<Point> requested_second_point;
    std::optional<Point> first_point;
    std::optional<Point> second_point;
    std::string package_id;
    struct PackageCommand
    {
        std::int32_t operation{};
        std::int32_t exit_code{-1};
        std::int32_t error_code{};
        bool started{};
        bool timed_out{};
        bool canceled{};
        bool process_running{};
        std::string standard_output;
        std::string standard_error;
        std::string error_message;
    };
    std::optional<PackageCommand> package_command;
};

enum class PackageOperationKind : std::int32_t
{
    start_package = 0,
    start_activity = 1,
    stop_package = 2,
    query_running = 3,
};

struct DeviceSessionOptions
{
    enum class ScreenshotMethod
    {
        automatic,
        raw,
        gzip,
        png,
        shell_png,
    };

    enum class TouchBackend
    {
        adb,
        minitouch,
        maatouch,
    };

    std::optional<std::int32_t> display_id;
    std::optional<std::string> surface_display_id;
    ScreenshotMethod screenshot_method{ScreenshotMethod::automatic};
    double swipe_duration_multiplier{10.0};
    std::int32_t extra_swipe_distance{100};
    std::int32_t extra_swipe_duration_ms{500};
    TouchBackend touch_backend{TouchBackend::adb};
    std::filesystem::path touch_binary_path;
    std::string touch_remote_path{"/data/local/tmp/uma-touch"};
    std::optional<std::string> touch_event_id;
};

enum class HeldTouchPhase
{
    down = 0,
    up = 1,
    cancel = 2,
};

class DeviceSession final
{
public:
    DeviceSession(
        std::shared_ptr<ConnectionProfile const> profile,
        std::filesystem::path adb_path,
        std::string profile_name,
        ConnectedDevice device,
        std::uint64_t session_generation,
        DeviceSessionOptions options = {});
    ~DeviceSession() noexcept;

    [[nodiscard]] std::string serial() const
    {
        std::lock_guard lock(mutex_);
        return device_.serial;
    }

    [[nodiscard]] std::optional<SessionFailure> update_options(DeviceSessionOptions options);

    [[nodiscard]] DeviceOperationResult verify_game(
        std::string const& package_id,
        std::stop_token cancellation);
    [[nodiscard]] DeviceOperationResult capture(
        std::stop_token cancellation, bool retain_pixels = false);
    [[nodiscard]] DeviceOperationResult tap(
        std::uint64_t frame_id,
        std::int32_t canonical_x,
        std::int32_t canonical_y,
        std::stop_token cancellation);
    [[nodiscard]] DeviceOperationResult tap_coordinate(
        std::int32_t x, std::int32_t y,
        std::int32_t reference_width, std::int32_t reference_height,
        std::stop_token cancellation);
    [[nodiscard]] DeviceOperationResult swipe(
        std::uint64_t frame_id,
        std::int32_t x1,
        std::int32_t y1,
        std::int32_t x2,
        std::int32_t y2,
        std::int32_t duration_ms,
        bool extra_swipe,
        std::stop_token cancellation);
    [[nodiscard]] DeviceOperationResult touch(
        std::uint64_t frame_id,
        std::int32_t canonical_x,
        std::int32_t canonical_y,
        HeldTouchPhase phase,
        std::stop_token cancellation);
    [[nodiscard]] DeviceOperationResult prepare_touch(
        std::uint64_t frame_id,
        std::stop_token cancellation);
    [[nodiscard]] DeviceOperationResult key_event(
        std::uint64_t frame_id,
        std::string const& key_code,
        std::stop_token cancellation);
    [[nodiscard]] DeviceOperationResult input_text(
        std::uint64_t frame_id,
        std::string const& text,
        std::stop_token cancellation);
    [[nodiscard]] DeviceOperationResult package_operation(
        PackageOperationKind operation,
        std::string const& package_id,
        std::string const& activity,
        std::stop_token cancellation);

    [[nodiscard]] bool connected(std::stop_token cancellation);
    [[nodiscard]] bool listed_as_ready(std::stop_token cancellation);
    [[nodiscard]] std::uint64_t session_generation() const noexcept;
    [[nodiscard]] std::optional<DeviceFrameMetadata> frame_metadata(std::uint64_t frame_id) const;
    [[nodiscard]] std::shared_ptr<const Vision::GrayImage> frame_pixels(std::uint64_t frame_id) const;
    [[nodiscard]] bool copy_frame_png(
        std::uint64_t frame_id,
        std::uint8_t* destination,
        std::uint64_t capacity) const noexcept;
    [[nodiscard]] bool release_frame(std::uint64_t frame_id) noexcept;

private:
    [[nodiscard]] DeviceOperationResult tap_on_geometry(
        DeviceFrameMetadata const& source_frame,
        std::int32_t canonical_x, std::int32_t canonical_y,
        std::stop_token cancellation);
    [[nodiscard]] std::optional<SessionFailure> run_command(
        std::vector<std::string> arguments,
        std::chrono::milliseconds timeout,
        std::stop_token cancellation,
        std::size_t max_standard_output_bytes,
        std::string& standard_output,
        bool leave_children_running_on_success = false,
        AdbCommandResult* command_result = nullptr);
    [[nodiscard]] std::optional<std::pair<std::int32_t, std::int32_t>> current_size(
        std::stop_token cancellation,
        std::optional<SessionFailure>& failure);
    struct Geometry
    {
        std::int32_t width{};
        std::int32_t height{};
        std::int32_t orientation{};
        friend bool operator==(Geometry const&, Geometry const&) = default;
    };
    enum class CaptureMethod
    {
        exec_png,
        exec_raw,
        shell_gzip_raw,
        shell_png,
    };
    [[nodiscard]] std::optional<Geometry> current_geometry(
        std::stop_token cancellation,
        std::optional<SessionFailure>& failure);
    [[nodiscard]] static std::optional<std::pair<std::int32_t, std::int32_t>> decode_png(
        std::vector<std::uint8_t> const& bytes, Vision::GrayImage* pixels = nullptr);
    struct HeldTouchDevice
    {
        std::string path;
        std::int32_t max_x{};
        std::int32_t max_y{};
        bool has_tool_finger{};
        bool has_touch{};
    };
    struct InteractiveTouch
    {
        std::unique_ptr<IWin32InteractiveSession> process;
        std::int32_t contacts{};
        std::int32_t max_x{};
        std::int32_t max_y{};
        std::int32_t max_pressure{};
        std::uint32_t remote_pid{};
        std::string remote_start_token;
        std::int32_t screen_width{};
        std::int32_t screen_height{};
        std::int32_t orientation{};
        bool pressed{};
    };
    struct PreparedTouch
    {
        std::uint64_t session_generation{};
        std::uint64_t geometry_generation{};
        DeviceSessionOptions::TouchBackend backend{DeviceSessionOptions::TouchBackend::adb};
        std::int32_t screen_width{};
        std::int32_t screen_height{};
        std::int32_t orientation{};
    };
    [[nodiscard]] std::optional<SessionFailure> ensure_interactive_touch(
        std::int32_t screen_width,
        std::int32_t screen_height,
        std::int32_t orientation,
        std::stop_token cancellation);
    [[nodiscard]] std::optional<SessionFailure> stop_interactive_touch(
        bool release_first) noexcept;
    [[nodiscard]] std::optional<SessionFailure> write_touch_protocol(
        std::string const& bytes, std::stop_token cancellation) noexcept;
    [[nodiscard]] std::pair<std::int32_t, std::int32_t> scale_touch_point(
        std::int32_t x, std::int32_t y,
        std::int32_t screen_width, std::int32_t screen_height,
        std::int32_t orientation) const noexcept;
    [[nodiscard]] std::optional<HeldTouchDevice> find_touch_device(
        std::stop_token cancellation,
        std::optional<SessionFailure>& failure);
    [[nodiscard]] std::optional<SessionFailure> release_held_touch(
        std::stop_token cancellation) noexcept;

    std::shared_ptr<ConnectionProfile const> profile_;
    std::filesystem::path adb_path_;
    std::string profile_name_;
    ConnectedDevice device_;
    std::uint64_t session_generation_{};
    DeviceSessionOptions options_;
    mutable std::mutex mutex_;
    std::unordered_map<std::uint64_t, DeviceFrame> frames_;
    std::optional<Geometry> last_geometry_;
    std::uint64_t geometry_generation_{};
    std::optional<CaptureMethod> capture_method_;
    bool geometry_batch_supported_{true};
    std::optional<HeldTouchDevice> held_touch_;
    std::optional<DeviceFrameMetadata> held_touch_frame_;
    std::optional<InteractiveTouch> interactive_touch_;
    std::optional<DeviceFrameMetadata> interactive_touch_frame_;
    std::optional<PreparedTouch> prepared_touch_;
    std::optional<HeldTouchDevice> prepared_touch_device_;
    std::uint64_t last_touch_frame_id_{};
};

}

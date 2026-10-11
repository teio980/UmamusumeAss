#pragma once

#include "UmaAssistant/UmaCaller.h"
#include "Resources/ResourceSnapshot.hpp"

#include <cstdint>
#include <string>

namespace UmaAssistant::CoreApi {

[[nodiscard]] UmaHandle create_handle(UmaApiCallback callback, void* custom_arg);
void destroy_handle(UmaHandle handle) noexcept;
[[nodiscard]] UmaStartResult start_connect(
    UmaHandle handle, std::string adb_path, std::string serial, std::string profile);
[[nodiscard]] UmaStartResult start_verify_game(UmaHandle handle, std::string package_id);
[[nodiscard]] UmaStartResult start_capture(UmaHandle handle);
[[nodiscard]] std::int32_t get_frame_png_size(
    UmaHandle handle, std::uint64_t frame_id, std::uint64_t* size) noexcept;
[[nodiscard]] std::int32_t copy_frame_png(
    UmaHandle handle, std::uint64_t frame_id,
    std::uint8_t* destination, std::uint64_t capacity) noexcept;
[[nodiscard]] std::int32_t release_frame(
    UmaHandle handle, std::uint64_t frame_id) noexcept;
[[nodiscard]] UmaStartResult start_tap(
    UmaHandle handle, std::uint64_t frame_id,
    std::int32_t x, std::int32_t y);
[[nodiscard]] UmaStartResult start_swipe(
    UmaHandle handle, std::uint64_t frame_id,
    std::int32_t x1, std::int32_t y1, std::int32_t x2, std::int32_t y2,
    std::int32_t duration_ms, bool extra_swipe = false);
[[nodiscard]] UmaStartResult start_touch(
    UmaHandle handle, std::uint64_t frame_id,
    std::int32_t x, std::int32_t y, std::int32_t phase);
[[nodiscard]] UmaStartResult start_prepare_touch(
    UmaHandle handle, std::uint64_t frame_id);
[[nodiscard]] UmaStartResult start_key_event(
    UmaHandle handle, std::uint64_t frame_id, std::string key_code);
[[nodiscard]] UmaStartResult start_input_text(
    UmaHandle handle, std::uint64_t frame_id, std::string text);
[[nodiscard]] UmaStartResult start_package_operation(
    UmaHandle handle, std::string package_id, std::string activity, std::int32_t operation);
[[nodiscard]] std::int32_t cancel_operation(
    UmaHandle handle, std::uint64_t operation_id) noexcept;
[[nodiscard]] std::int32_t set_instance_options(
    UmaHandle handle, std::string const& utf8_json) noexcept;
[[nodiscard]] std::int32_t match_template(
    UmaHandle handle, UmaImageView const* screen, UmaImageView const* template_image,
    UmaTemplateMatchOptions const* options, UmaTemplateMatchResult* result) noexcept;
[[nodiscard]] std::int32_t recognize_text(
    UmaHandle handle, UmaImageView const* image, char const* utf8_language,
    char* json_destination, std::uint64_t capacity,
    std::uint64_t* required_length) noexcept;
[[nodiscard]] std::int32_t compare_image_change(
    UmaHandle handle, UmaImageView const* before, UmaImageView const* after,
    UmaTemplateMatchResult const* match, std::int32_t* changed) noexcept;
[[nodiscard]] std::int32_t compare_persistent_image_change(
    UmaHandle handle, UmaImageView const* before, UmaImageView const* first_after,
    UmaImageView const* settled_after, UmaTemplateMatchResult const* match,
    std::int32_t* changed) noexcept;

[[nodiscard]] std::int32_t load_template_image(UmaHandle handle, char const* path,
    std::int32_t preparation, std::shared_ptr<Resources::PreparedTemplate const>& image) noexcept;
[[nodiscard]] std::int32_t refresh_resource_snapshot(UmaHandle handle) noexcept;
[[nodiscard]] std::int32_t transform_image(UmaImageView const* source,
    UmaImageTransformOptions const* options, Vision::GrayImage& image) noexcept;
[[nodiscard]] std::int32_t binarize_color(UmaImageView const* source,
    UmaColorRule const* rule, Vision::GrayImage& image) noexcept;
[[nodiscard]] std::int32_t find_color_blobs(UmaImageView const* source, UmaColorRule const* rule,
    UmaImageRect const* region, UmaColorBlob* destination, std::uint64_t capacity,
    std::uint64_t* required_count) noexcept;
[[nodiscard]] std::int32_t count_color(UmaImageView const* source, UmaColorRule const* rule,
    UmaImageRect const* region, UmaImageRect const* exclude, std::uint64_t* matching,
    std::uint64_t* sampled) noexcept;
[[nodiscard]] std::int32_t prepare_numeric_glyphs(UmaImageView const* source, int warm_gain,
    UmaColorRule const* rule, Vision::GrayImage& image) noexcept;
[[nodiscard]] std::int32_t compose_gray_images(UmaImageView const* sources, UmaImageRect const* placements,
    std::uint64_t count, int width, int height, Vision::GrayImage& image) noexcept;
[[nodiscard]] std::int32_t count_ink_groups(UmaImageView const* source, int threshold, int* groups) noexcept;
[[nodiscard]] std::int32_t sample_image_difference(UmaImageView const* before, UmaImageView const* after,
    UmaImagePoint const* points, std::uint64_t count, std::uint64_t* difference) noexcept;
[[nodiscard]] std::int32_t validate_pipeline_json(char const* utf8_json, char* error_destination,
    std::uint64_t capacity, std::uint64_t* required_length) noexcept;
[[nodiscard]] UmaStartResult start_run_pipeline(UmaHandle handle, std::string pipeline_json_or_path,
    std::string entry_task);
[[nodiscard]] UmaStartResult start_run_game_task(UmaHandle handle, std::string task_id,
    std::string settings_json);

[[nodiscard]] UmaAppendTaskResult append_task(
    UmaHandle handle, char const* task_id, char const* settings_json) noexcept;
[[nodiscard]] std::int32_t set_task_params(
    UmaHandle handle, std::uint64_t task_id, char const* settings_json) noexcept;
[[nodiscard]] std::int32_t clear_tasks(UmaHandle handle) noexcept;
[[nodiscard]] UmaStartResult start_queue(UmaHandle handle);
[[nodiscard]] std::int32_t get_run_snapshot_json(
    UmaHandle handle, char* destination, std::uint64_t capacity, std::uint64_t* required_length) noexcept;
[[nodiscard]] std::int32_t get_catalog_json(
    UmaHandle handle, char const* catalog_type, char* destination, std::uint64_t capacity, std::uint64_t* required_length) noexcept;

}

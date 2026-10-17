#pragma once

#include "ITaskExecutionContext.hpp"
#include "Session/DeviceSession.hpp"
#include "Resources/ResourceSnapshot.hpp"
#include "Recognition/WindowsOcrRecognizer.hpp"
#include "Api/CallbackSender.hpp"
#include "Vision/TemplateMatcher.hpp"

#include <memory>
#include <string>
#include <vector>
#include <optional>
#include <stop_token>
#include <filesystem>
#include <chrono>
#include <functional>

namespace UmaAssistant::TaskEngine
{

class NativeTaskExecutionContext final : public ITaskExecutionContext
{
public:
    NativeTaskExecutionContext(
        std::shared_ptr<DeviceSession> session,
        std::shared_ptr<Resources::ResourceSnapshot const> resources,
        Recognition::WindowsOcrRecognizer* ocr,
        Api::CallbackSender* sender,
        std::uint64_t operation_id,
        std::stop_token cancellation,
        std::filesystem::path resource_base_dir,
        std::string event_task_id = {},
        std::uint64_t task_instance_id = 0,
        std::function<void(std::int32_t, std::int32_t)> progress_callback = {});

    ~NativeTaskExecutionContext() override;

    bool CaptureScreen() override;
    [[nodiscard]] std::shared_ptr<const TaskFrameSnapshot> CaptureInspectionFrame() override;

    [[nodiscard]] std::string GetDeviceSerial() const override;

    TaskMatchResult MatchTemplate(
        const std::string& templatePath,
        double threshold,
        const std::optional<TaskRoi>& roi = std::nullopt,
        const std::string& algorithm = "MatchTemplate") override;

    TaskMatchResult MatchTemplateAdvanced(
        const std::string& templatePath,
        double threshold,
        const std::optional<TaskRoi>& roi,
        const std::string& algorithm,
        int referenceWidth,
        int referenceHeight,
        const std::vector<double>& scaleCandidates) override;

    [[nodiscard]] std::shared_ptr<const TaskFrameSnapshot> GetCurrentFrame() const override;
    [[nodiscard]] std::shared_ptr<const TaskFrameSnapshot> GetPreviousFrame() const override;
    [[nodiscard]] std::shared_ptr<const Vision::GrayImage> LoadTemplateImage(const std::string& path) override;
    void SetCoordinateReference(int width, int height) override;
    [[nodiscard]] std::pair<int, int> GetCoordinateReference() const override;
    [[nodiscard]] std::string GetTemplateBaseDirectory() const override;
    void SetTemplateBaseDirectory(const std::string& directory) override;
    [[nodiscard]] bool TapMatch(int x, int y, const TaskMatchResult& match) override;
    [[nodiscard]] bool TapCoordinate(int x, int y) override;
    [[nodiscard]] bool RetainMatchForRetry(
        const TaskMatchResult& match,
        int maxTapAttempts = 1,
        int lifetimeMilliseconds = 30000,
        int fixedReferenceX = -1,
        int fixedReferenceY = -1) override;
    void ReleaseRetainedMatch() override;
    [[nodiscard]] bool HasRetainedMatch() const override;
    [[nodiscard]] bool IsMatchRetained(const TaskMatchResult& match) const override;

    bool CheckImageChange(const TaskMatchResult& match) override;

    std::vector<TaskOcrDetection> RecognizeText(
        const std::string& language,
        const std::optional<TaskRoi>& roi = std::nullopt) override;

    std::vector<TaskOcrDetection> RecognizeImageText(
        const Vision::GrayImage& image, const std::string& language) override;
    Recognition::NumericOcrBatchResult RecognizeNumericImages(
        const std::vector<Recognition::NumericOcrBatchField>& fields) override;
    [[nodiscard]] std::shared_ptr<IImageRecognitionContext> CreateImageRecognitionContext() override;
    bool PrepareHeldTouch() override;
    bool TouchDownMatch(int x, int y, const TaskMatchResult& source) override;
    bool TouchUp() override;
    bool TouchCancel() override;

    bool ProbeHsv(
        int x, int y, int radius,
        double hMin, double hMax,
        double sMin, double sMax,
        double vMin, double vMax,
        double minRatio) override;

    bool Tap(int x, int y) override;

    bool Swipe(int x1, int y1, int x2, int y2, int durationMs) override;

    bool InputText(const std::string& text) override;

    bool SendKey(const std::string& keyCode) override;

    void Sleep(int milliseconds) override;

    bool IsCancelled() const override;
    [[nodiscard]] int GetLastErrorCode() const override;

    void Log(const std::string& level, const std::string& message) override;

    void EmitEvent(const TaskExecutionEvent& event) override;

private:
    void LogDeviceTimings(const char* operation, const DeviceOperationResult& result);
    std::filesystem::path ResolveTemplatePath(const std::string& path) const;
    bool PrepareCurrentFrameInput();
    bool ReleaseHeldTouch(HeldTouchPhase phase);

    std::shared_ptr<DeviceSession> m_session;
    std::shared_ptr<Resources::ResourceSnapshot const> m_resources;
    Recognition::WindowsOcrRecognizer* m_ocr{nullptr};
    std::unique_ptr<Recognition::NumericOcrBatch> m_numeric_ocr;
    std::shared_ptr<IImageRecognitionContext> m_image_recognition;
    Api::CallbackSender* m_sender{nullptr};
    std::uint64_t m_operation_id{0};
    std::stop_token m_cancellation;
    std::filesystem::path m_resource_base_dir;
    std::filesystem::path m_template_base_dir;
    std::string m_event_task_id;
    std::uint64_t m_task_instance_id{0};
    std::function<void(std::int32_t, std::int32_t)> m_progress_callback;
    int m_reference_width{900};
    int m_reference_height{1600};

    std::uint64_t m_current_frame_id{0};
    std::uint64_t m_retained_frame_id{0};
    std::uint64_t m_held_frame_id{0};
    int m_held_canonical_x{0};
    int m_held_canonical_y{0};
    int m_retained_taps_remaining{0};
    std::chrono::steady_clock::time_point m_retained_match_expires{};
    std::chrono::steady_clock::time_point m_current_frame_captured_at{};
    int m_retained_reference_x{0};
    int m_retained_reference_y{0};
    int m_retained_reference_width{900};
    int m_retained_reference_height{1600};
    int m_retained_canonical_x{0};
    int m_retained_canonical_y{0};
    bool m_current_frame_input_consumed{false};
    int m_last_error_code{0};
    std::optional<DeviceFrameMetadata> m_current_metadata;
    std::shared_ptr<const Vision::GrayImage> m_current_screen;
    std::shared_ptr<const Vision::GrayImage> m_previous_screen;
    std::shared_ptr<const TaskFrameSnapshot> m_current_frame_snapshot;
    std::shared_ptr<const TaskFrameSnapshot> m_previous_frame_snapshot;
};

} // namespace UmaAssistant::TaskEngine

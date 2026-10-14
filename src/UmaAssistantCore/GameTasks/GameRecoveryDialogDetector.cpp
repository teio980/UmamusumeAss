#include "GameRecoveryDialogDetector.hpp"
#include "Common/PathEncoding.hpp"
#include "Resources/ImageTransforms.hpp"

#include <array>
#include <algorithm>
#include <cstdlib>

namespace UmaAssistant::GameTasks
{
GameRecoveryDialogMatch DetectGameRecoveryDialog(
    const TaskEngine::TaskFrameSnapshot* frame,
    const Resources::ResourceSnapshot* resources,
    const std::filesystem::path& resourceRoot)
{
    using Kind = GameRecoveryDialogKind;
    if (!resources) return {Kind::missingTemplates, {}};
    auto const base = resourceRoot / "hachimi/common/date_changed";
    constexpr std::array names{"title.png", "body.png", "ok.png", "err.png", "titlescreen.png",
        "session_error_title.png", "session_error_body.png", "session_error_title_screen.png"};
    std::array<std::shared_ptr<const Resources::PreparedTemplate>, names.size()> templates;
    for (std::size_t index = 0; index < names.size(); ++index)
    {
        templates[index] = resources->load_template(Paths::ToUtf8(base / names[index]),
            Resources::TemplatePreparation::original);
        if (!templates[index]) return {Kind::missingTemplates, {}};
    }
    if (!frame || frame->width <= 0 || frame->height <= 0) return {};
    auto const count = static_cast<std::size_t>(frame->width) * static_cast<std::size_t>(frame->height);
    if (frame->grayPixels.size() != count || frame->rgbaPixels.size() != count * 4) return {};
    Vision::GrayImage image{frame->width, frame->height, frame->grayPixels, frame->rgbaPixels};
    // main normalizes the complete colored frame, rather than only scaling ROIs.
    if (image.width != 900 || image.height != 1600)
        image = Resources::resize_image(image, 900, 1600);
    auto gray = [&](std::size_t index, std::array<std::int32_t, 4> roi, double threshold) {
        // Tight lettering must include odd pixel offsets inside the ROI.
        return Vision::find(image, templates[index]->image, roi, threshold, 900, 1600, 1);
    };
    auto buttonMatch = [&](Kind kind, const Vision::MatchResult& button) {
        TaskEngine::TaskMatchResult match{button.found, button.score, button.x, button.y,
            button.width, button.height, 900, 1600, frame->frameId,
            frame->sessionGeneration, frame->geometryGeneration};
        return GameRecoveryDialogMatch{kind, match};
    };
    if (gray(0, {310, 475, 280, 80}, 0.92).found
        && gray(1, {325, 730, 250, 85}, 0.90).found)
    {
        auto const button = Vision::find_color(image, templates[2]->image,
            std::array<std::int32_t, 4>{250, 975, 400, 130}, 0.92, 900, 1600);
        if (button.found) return buttonMatch(Kind::dateChanged, button);
    }
    if (gray(3, {300, 485, 300, 55}, 0.92).found)
    {
        auto const button = gray(4, {120, 1000, 260, 80}, 0.92);
        if (button.found) return buttonMatch(Kind::connectionError, button);
    }
    // Brown lettering must actually be present. Gray correlation's constant
    // background fallback alone can otherwise accept an empty white panel.
    auto brownText = [&](std::size_t index, std::array<std::int32_t, 4> roi, double threshold) {
        auto match = gray(index, roi, threshold);
        if (!match.found) return match;
        auto const& marker = templates[index]->image;
        double error = 0;
        int pixels = 0;
        for (int y = 0; y < marker.height; ++y)
            for (int x = 0; x < marker.width; ++x)
            {
                auto const offset = (static_cast<std::size_t>(y) * marker.width + x) * 4;
                int const r = marker.rgba[offset], g = marker.rgba[offset + 1], b = marker.rgba[offset + 2];
                if (r <= g || g <= b || r - b < 40) continue;
                auto const actual = (static_cast<std::size_t>(match.y + y) * image.width + match.x + x) * 4;
                error += std::abs(static_cast<int>(image.rgba[actual]) - r)
                    + std::abs(static_cast<int>(image.rgba[actual + 1]) - g)
                    + std::abs(static_cast<int>(image.rgba[actual + 2]) - b);
                ++pixels;
            }
        match.found = pixels > 0 && 1 - error / (pixels * 765.0) >= threshold;
        return match;
    };
    auto sessionButton = brownText(7, {320, 1000, 260, 90}, 0.92);
    if (sessionButton.found) return buttonMatch(Kind::sessionError, sessionButton);
    auto sessionTitle = gray(5, {320, 475, 260, 80}, 0.92);
    if (sessionTitle.found)
    {
        auto result = buttonMatch(Kind::sessionError, sessionTitle);
        result.tapX = sessionTitle.x + (450 - 345);
        result.tapY = sessionTitle.y + (1043 - 492);
        return result;
    }
    auto sessionBody = brownText(6, {140, 730, 620, 100}, 0.90);
    if (sessionBody.found)
    {
        auto result = buttonMatch(Kind::sessionError, sessionBody);
        result.tapX = sessionBody.x + (450 - 160);
        result.tapY = sessionBody.y + (1043 - 750);
        return result;
    }
    return {};
}
}

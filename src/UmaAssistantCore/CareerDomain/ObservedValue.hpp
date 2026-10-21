#pragma once

#include <algorithm>
#include <cmath>
#include <optional>
#include <string>
#include <utility>

namespace UmaAssistant::CareerDomain
{

enum class ObservationSource
{
    Unknown,
    Ocr,
    TemplateMatch,
    ColorProbe,
    Derived,
    DefaultFallback
};

template <typename T>
struct ObservedValue
{
    std::optional<T> value;
    double confidence{0.0};
    ObservationSource source{ObservationSource::Unknown};
    std::string detail;

    [[nodiscard]] bool HasValue() const noexcept { return value.has_value(); }
    [[nodiscard]] const T& Value() const { return value.value(); }
    [[nodiscard]] T ValueOr(const T& fallback) const { return value.value_or(fallback); }
    [[nodiscard]] bool HasValidConfidence() const noexcept
    {
        return std::isfinite(confidence) && confidence >= 0.0 && confidence <= 1.0;
    }
    [[nodiscard]] bool IsReliable(double minimumConfidence = 0.0) const noexcept
    {
        return HasValue() && HasValidConfidence() && confidence > 0.0 && confidence >= minimumConfidence;
    }

    static ObservedValue<T> Make(T val, double conf = 1.0, ObservationSource src = ObservationSource::Derived, std::string det = "")
    {
        conf = std::isfinite(conf) ? std::clamp(conf, 0.0, 1.0) : 0.0;
        return ObservedValue<T>{std::move(val), conf, src, std::move(det)};
    }

    static ObservedValue<T> Unknown()
    {
        return ObservedValue<T>{std::nullopt, 0.0, ObservationSource::Unknown, ""};
    }
};

} // namespace UmaAssistant::CareerDomain

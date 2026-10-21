#pragma once

#include <cstdint>
#include <vector>

namespace UmaAssistant::TaskEngine
{

struct TaskFrameSnapshot
{
    int width{0};
    int height{0};
    int orientation{0};
    std::uint64_t frameId{0};
    std::uint64_t sessionGeneration{0};
    std::uint64_t geometryGeneration{0};
    std::vector<std::uint8_t> grayPixels;
    std::vector<std::uint8_t> rgbaPixels;
};

} // namespace UmaAssistant::TaskEngine

// Stub for libpyin: worldline only needs synthesis here, not the pyin F0
// estimator.  Returns an empty contour when F0(method=2) is requested.

#include <vector>

#include "worldline/f0/pyin_estimator.h"

namespace worldline {

void PyinEstimator::Estimate(const std::vector<double>& samples, int fs,
                             double frame_ms, std::vector<double>* f0,
                             std::vector<double>* time_axis) {
  (void) samples;
  (void) fs;
  (void) frame_ms;
  f0->clear();
  time_axis->clear();
}

}  // namespace worldline


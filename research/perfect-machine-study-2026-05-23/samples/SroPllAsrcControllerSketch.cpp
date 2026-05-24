// Sketch only. Control-rate PLL for driving an ASRC/fractional delay actuator.

#include <algorithm>
#include <cmath>

struct SyncObservation
{
    double delaySamples;
    double confidence;
    double dtSeconds;
};

struct ActuatorCommand
{
    double targetDelaySamples;
    double resampleRatio;
    double confidence;
};

class SroPllAsrcController
{
public:
    ActuatorCommand update(const SyncObservation& obs)
    {
        if (obs.confidence < 0.05 || obs.dtSeconds <= 0.0)
        {
            return command(obs.confidence);
        }

        double error = obs.delaySamples - delaySamples_;
        double alpha = clamp(obs.confidence * 0.08, 0.005, 0.05);
        double beta = clamp(obs.confidence * 0.01, 0.0005, 0.006);

        delaySamples_ += alpha * error;
        sroPpm_ += beta * error / obs.dtSeconds;
        sroPpm_ = clamp(sroPpm_, -300.0, 300.0);
        confidence_ += (obs.confidence - confidence_) * 0.05;

        return command(confidence_);
    }

private:
    double delaySamples_ = 0.0;
    double sroPpm_ = 0.0;
    double confidence_ = 0.0;

    ActuatorCommand command(double confidence) const
    {
        // Positive SRO means candidate appears to drift later; playback/capture
        // correction convention must be fixed against the DSP graph before use.
        double ratio = 1.0 - sroPpm_ * 1.0e-6;
        return { delaySamples_, ratio, confidence };
    }

    static double clamp(double value, double lo, double hi)
    {
        return std::max(lo, std::min(hi, value));
    }
};


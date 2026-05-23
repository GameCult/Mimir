// Sketch only. Cubic Farrow fractional-delay actuator for small delay changes.

#include <algorithm>
#include <array>
#include <cmath>

class FarrowFractionalDelay
{
public:
    float process(float x, double delaySamples)
    {
        push(x);

        int whole = static_cast<int>(std::floor(delaySamples));
        double mu = delaySamples - whole;

        float xm1 = read(whole + 3);
        float x0  = read(whole + 2);
        float x1  = read(whole + 1);
        float x2  = read(whole + 0);

        // Cubic Lagrange form. Good enough for an actuator proof; production
        // should compare against polyphase sinc for final program quality.
        double c0 = x0;
        double c1 = 0.5 * (x1 - xm1);
        double c2 = xm1 - 2.5 * x0 + 2.0 * x1 - 0.5 * x2;
        double c3 = 0.5 * (x2 - xm1) + 1.5 * (x0 - x1);
        return static_cast<float>(((c3 * mu + c2) * mu + c1) * mu + c0);
    }

private:
    std::array<float, 4096> ring{};
    unsigned cursor = 0;

    void push(float x)
    {
        ring[cursor++ & (ring.size() - 1)] = x;
    }

    float read(int delay) const
    {
        return ring[(cursor - 1 - static_cast<unsigned>(delay)) & (ring.size() - 1)];
    }
};


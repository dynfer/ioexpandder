#include "util.h"
#include <algorithm>
#include "config.h"
#include "math.h"

static float interpolateSensor(float volt, const analogCal &cal)
{
    float lowVal = cal.lowCal;
    float highVal = cal.highCal;
    float lowVolt = cal.lowV;
    float highVolt = cal.highV;

    if (highVolt == lowVolt)
    {
        return lowVal;
    }

    float normalized = (volt - lowVolt) / (highVolt - lowVolt);

    normalized = std::clamp(normalized, 0.0f, 1.0f);

    return lowVal + normalized * (highVal - lowVal);
}

static float ntcTempFromVolt(float volt, const ntcCal &cal)
{
    constexpr float Vref = 5.0f;
    constexpr float pullupR = 2700.0f;

    if (volt <= 0.0f || volt >= Vref)
    {
        return 0;
    }

    float r_ntc = pullupR * volt / (Vref - volt);

    if (r_ntc <= 0.0f)
    {
        return 0;
    }

    float lnR = std::log(r_ntc);
    float invT = cal.r1 + cal.r2 * lnR + cal.r3 * lnR * lnR * lnR;

    if (invT == 0.0f)
    {
        return 0;
    }

    float T_k = 1.0f / invT;
    float T_c = T_k - 273.15f;
    return T_c;
}

uint16_t getOutputValue(uint16_t raw, size_t idx, bool ntc)
{
    config &g_config = getConfig();
    const analogCal &g_analogConfig = g_config.getAnalogConfig(idx);
    const ntcCal &g_ntcConfig = g_config.getNtcConfig(idx);

    scaling factor = g_analogConfig.factor;

    if (ntc)
    {
        return static_cast<uint16_t>(ntcTempFromVolt(static_cast<float>(raw / 1000.0f), g_ntcConfig) + 100);
    }
    else
    {
        float value = interpolateSensor(static_cast<float>(raw / 1000.0f), g_analogConfig);
        switch (factor)
        {
        case scaling::x1:
            return static_cast<uint16_t>(value * 10000.0f);
            break;
        case scaling::x10:
            return static_cast<uint16_t>(value * 1000.0f);
            break;
        case scaling::x100:
            return static_cast<uint16_t>(value * 100.0f);
            break;
        case scaling::x1000:
            return static_cast<uint16_t>(value * 10.0f);
            break;
        case scaling::x10000:
            return static_cast<uint16_t>(value * 1.0f);
            break;
        default:
            return 0;
            break;
        }
    }
}
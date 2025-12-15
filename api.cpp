#include "api.h"
#include "config.h"
#include "usbcfg.h"

api::api()
{
    m_outVolts.fill(0);
    m_outVals.fill(0);
    m_avCals.fill(0);
    m_avCalsVolt.fill(0);
    m_ntcCals.fill(0);
    m_ntcCalsTemp.fill(0);
    m_calsBuffer.fill(0);
    m_factors.fill(0);
    m_outVals[0] = static_cast<uint8_t>(apiresponse::dataResponse);
    m_outVolts[0] = static_cast<uint8_t>(apiresponse::voltsResponse);
    m_avCals[0] = static_cast<uint8_t>(apiresponse::avCalsResponse);
    m_avCalsVolt[0] = static_cast<uint8_t>(apiresponse::avCalsVoltResponse);
    m_ntcCals[0] = static_cast<uint8_t>(apiresponse::ntcCalsResponse);
    m_ntcCalsTemp[0] = static_cast<uint8_t>(apiresponse::ntcCalsTempResponse);
    m_factors[0] = static_cast<uint8_t>(apiresponse::factorResponse);
}

void api::getData()
{
    inputs &g_inputs = getInputs();

    for (size_t i = 1; i < m_outVals.size(); i += 2)
    {
        const size_t ch = (i - 1) / 2;

        uint16_t raw, mv;

        if (ch < 6) {
            raw = g_inputs.getAnalogInputValue(ch);
            mv  = g_inputs.getAnalogVolt(ch);
        } else {
            const size_t t = ch - 6;   // 0..3
            raw = g_inputs.getAnalogTempInputValue(t);
            mv  = g_inputs.getAnalogTempVolt(t);
        }

        m_outVals[i]     = raw & 0xFF;
        m_outVals[i + 1] = raw >> 8;

        m_outVolts[i]     = mv & 0xFF;
        m_outVolts[i + 1] = mv >> 8;
    }
}

void api::getCals()
{
    const config &g_config = getConfig();
    for (size_t i = 1; i < m_avCals.size(); i += 4)
    {
        const analogCal &cal = g_config.getAnalogConfig(i / 4);
        m_avCals[i] = cal.lowCal & 0xFF;
        m_avCals[i + 1] = cal.lowCal >> 8;
        m_avCals[i + 2] = cal.highCal & 0xFF;
        m_avCals[i + 3] = cal.highCal >> 8;
    }
    for (size_t i = 1; i < m_avCalsVolt.size(); i += 4)
    {
        const analogCal &cal = g_config.getAnalogConfig(i / 4);
        m_avCalsVolt[i] = cal.lowV & 0xFF;
        m_avCalsVolt[i + 1] = cal.lowV >> 8;
        m_avCalsVolt[i + 2] = cal.highV & 0xFF;
        m_avCalsVolt[i + 3] = cal.highV >> 8;
    }
    for (size_t i = 1; i < m_ntcCals.size(); i += 12)
    {
        const ntcCal &cal = g_config.getNtcConfig(i / 12);
        m_ntcCals[i] = cal.r1 & 0xFF;
        m_ntcCals[i + 1] = (cal.r1 >> 8) & 0xFF;
        m_ntcCals[i + 2] = (cal.r1 >> 16) & 0xFF;
        m_ntcCals[i + 3] = (cal.r1 >> 24) & 0xFF;
        m_ntcCals[i + 4] = cal.r2 & 0xFF;
        m_ntcCals[i + 5] = (cal.r2 >> 8) & 0xFF;
        m_ntcCals[i + 6] = (cal.r2 >> 16) & 0xFF;
        m_ntcCals[i + 7] = (cal.r2 >> 24) & 0xFF;
        m_ntcCals[i + 8] = cal.r3 & 0xFF;
        m_ntcCals[i + 9] = (cal.r3 >> 8) & 0xFF;
        m_ntcCals[i + 10] = (cal.r3 >> 16) & 0xFF;
        m_ntcCals[i + 11] = (cal.r3 >> 24) & 0xFF;
    }
    for (size_t i = 1; i < m_ntcCalsTemp.size(); i += 6)
    {
        const ntcCal &cal = g_config.getNtcConfig(i / 6);
        uint16_t t1 = static_cast<uint16_t>(cal.t1);
        m_ntcCalsTemp[i] = t1 & 0xFF;
        m_ntcCalsTemp[i + 1] = t1 >> 8;
        uint16_t t2 = static_cast<uint16_t>(cal.t2);
        m_ntcCalsTemp[i + 2] = t2 & 0xFF;
        m_ntcCalsTemp[i + 3] = t2 >> 8;
        uint16_t t3 = static_cast<uint16_t>(cal.t3);
        m_ntcCalsTemp[i + 4] = t3 & 0xFF;
        m_ntcCalsTemp[i + 5] = t3 >> 8;
    }
    for (size_t i = 1; i < m_factors.size(); i++)
    {
        const analogCal &cal = g_config.getAnalogConfig(i - 1);
        m_factors[i] = static_cast<uint8_t>(cal.factor);
    }

}

void api::sendData()
{
    chnWrite(&SDU1, m_outVals.data(), m_outVals.size());
    chnWrite(&SDU1, m_outVolts.data(), m_outVolts.size());
}

void api::sendCals()
{
    chnWrite(&SDU1, m_avCals.data(), m_avCals.size());
    chnWrite(&SDU1, m_avCalsVolt.data(), m_avCalsVolt.size());
    chnWrite(&SDU1, m_ntcCals.data(), m_ntcCals.size());
    chnWrite(&SDU1, m_ntcCalsTemp.data(), m_ntcCalsTemp.size());
}

void api::writeCals()
{
    // Helper lambdas to decode little-endian fields safely (avoid signed-overflow UB on shifts)
    auto rd_u16 = [&](size_t off) -> uint16_t
    {
        return static_cast<uint16_t>(static_cast<uint16_t>(m_calsBuffer[off]) |
                                     (static_cast<uint16_t>(m_calsBuffer[off + 1]) << 8));
    };
    auto rd_i16 = [&](size_t off) -> int16_t
    {
        return static_cast<int16_t>(rd_u16(off));
    };
    auto rd_u32 = [&](size_t off) -> uint32_t
    {
        return (static_cast<uint32_t>(m_calsBuffer[off]) |
                (static_cast<uint32_t>(m_calsBuffer[off + 1]) << 8) |
                (static_cast<uint32_t>(m_calsBuffer[off + 2]) << 16) |
                (static_cast<uint32_t>(m_calsBuffer[off + 3]) << 24));
    };

    if (chnRead(&SDU1, m_calsBuffer.data(), m_calsBuffer.size()) != m_calsBuffer.size())
    {
        return;
    }

    if (m_calsBuffer[0] != static_cast<uint8_t>(apiresponse::avCalsResponse) ||
        m_calsBuffer[25] != static_cast<uint8_t>(apiresponse::avCalsVoltResponse) ||
        m_calsBuffer[50] != static_cast<uint8_t>(apiresponse::ntcCalsResponse) ||
        m_calsBuffer[99] != static_cast<uint8_t>(apiresponse::ntcCalsTempResponse) ||
        m_calsBuffer[124] != static_cast<uint8_t>(apiresponse::factorResponse))
    {
        return;
    }

    config &g_config = getConfig();

    // Layout in m_calsBuffer:
    //  0..24   : AV cals        (id + 6*(lowCal,highCal))
    // 25..49   : AV volts       (id + 6*(lowV,highV))
    // 50..98   : NTC resistances(id + 4*(r1,r2,r3))
    // 99..123  : NTC temps      (id + 4*(t1,t2,t3))
    // 124..130 : Scaling factors(id + factor)

    // --- Analog value calibration (low/high cal) ---
    for (size_t i = 1; i < m_avCals.size(); i += 4)
    {
        const size_t idx = i / 4;
        analogCal cal = g_config.getAnalogConfig(idx);
        cal.lowCal = rd_u16(i);
        cal.highCal = rd_u16(i + 2);
        g_config.setAnalogConfig(idx, cal);
    }

    // --- Analog voltage points (low/high volt) ---
    constexpr size_t AV_VOLTS_BASE = 25;
    for (size_t i = 1; i < m_avCalsVolt.size(); i += 4)
    {
        const size_t idx = i / 4;
        analogCal cal = g_config.getAnalogConfig(idx);
        cal.lowV = rd_u16(AV_VOLTS_BASE + i);
        cal.highV = rd_u16(AV_VOLTS_BASE + i + 2);
        g_config.setAnalogConfig(idx, cal);
    }

    // --- NTC resistances (r1/r2/r3) ---
    constexpr size_t NTC_R_BASE = 50;
    for (size_t i = 1; i < m_ntcCals.size(); i += 12)
    {
        const size_t idx = i / 12;
        ntcCal cal = g_config.getNtcConfig(idx);
        cal.r1 = rd_u32(NTC_R_BASE + i);
        cal.r2 = rd_u32(NTC_R_BASE + i + 4);
        cal.r3 = rd_u32(NTC_R_BASE + i + 8);
        g_config.setNtcConfig(idx, cal);
    }

    // --- NTC temperature points (t1/t2/t3) ---
    constexpr size_t NTC_T_BASE = 99;
    for (size_t i = 1; i < m_ntcCalsTemp.size(); i += 6)
    {
        const size_t idx = i / 6;
        ntcCal cal = g_config.getNtcConfig(idx);
        cal.t1 = rd_i16(NTC_T_BASE + i);
        cal.t2 = rd_i16(NTC_T_BASE + i + 2);
        cal.t3 = rd_i16(NTC_T_BASE + i + 4);
        g_config.setNtcConfig(idx, cal);
    }
    constexpr size_t FACTORS_BASE = 124;
    for (size_t i = 1; i < m_factors.size(); i++)
    {
        const size_t idx = i - 1;
        analogCal cal = g_config.getAnalogConfig(idx);
        cal.factor = static_cast<scaling>(m_factors[i]);
        g_config.setAnalogConfig(idx, cal);
    }

    g_config.save();
}
#pragma once
#include "hal.h"
#include "ch.h"
#include <array>

enum class scaling : uint8_t
{
    x1 = 0,
    x10,
    x100,
    x1000,
    x10000
};

struct analogCal
{
    uint16_t lowV;
    uint16_t highV;
    uint16_t lowCal;
    uint16_t highCal;
    scaling factor;
};

struct ntcCal
{
    uint32_t r1;
    uint32_t r2;
    uint32_t r3;
    int16_t t1;
    int16_t t2;
    int16_t t3;
};

class configAnalog
{
private:
    std::array<ntcCal, 4> m_ntcCals;
    std::array<analogCal, 6> m_analogCals;
public:
    const ntcCal& getNtcCal(size_t idx) const { return m_ntcCals[idx]; };
    const analogCal& getAnalogCal(size_t idx) const { return m_analogCals[idx]; };
};

class config
{
private:
    configAnalog m_analogConfig;
public:
    config();
    void loadDefault();
    void loadConfigFromFlash();
    void saveConfigToflash(uint8_t* buffer);
    const analogCal& getAnalogConfig(size_t idx) const { return m_analogConfig.getAnalogCal(idx); }
    const ntcCal& getNtcConfig(size_t idx) const { return m_analogConfig.getNtcCal(idx); }
};

config &getConfig();
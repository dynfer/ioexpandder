// config.h
#pragma once
#include "hal.h"
#include "ch.h"
#include <array>
#include <cstdint>

enum class scaling : uint8_t
{
    x1 = 0,
    x10,
    x100,
    x1000,
    x10000
};

enum class pullupVolt : uint8_t
{
    None = 0,
    V5,
    V12
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
    std::array<pullupVolt, 4> m_digitalPullups;

public:
    configAnalog();
    const ntcCal& getNtcCal(size_t idx) const { return m_ntcCals[idx]; };
    const analogCal& getAnalogCal(size_t idx) const { return m_analogCals[idx]; };
    analogCal& writeAnalogCal(size_t idx) { return m_analogCals[idx]; };
    ntcCal& writeNtcCal(size_t idx) { return m_ntcCals[idx]; };
    void writePullup(size_t idx, pullupVolt pu) { m_digitalPullups[idx] = pu; };
    const pullupVolt& getDigitalPullup(size_t idx) const { return m_digitalPullups[idx]; };
};

/* ---- NEW: flash format wrapper ---- */
struct ConfigFlashImage
{
    uint32_t magic;     // identifies valid config
    uint16_t version;   // bump when struct meaning changes
    uint16_t size;      // sizeof(configAnalog)
    uint32_t crc;       // CRC32 of configAnalog bytes
    configAnalog analog;
};

class config
{
private:
    configAnalog m_analogConfig;

    bool isFlashValid() const;
    void writeImageToFlash(const configAnalog& cfg);

public:
    config();

    void loadConfigFromFlash();
    void save();          // saves current m_analogConfig
    void factoryReset();  // write defaults once on request

    const analogCal& getAnalogConfig(size_t idx) const { return m_analogConfig.getAnalogCal(idx); }
    const ntcCal& getNtcConfig(size_t idx) const { return m_analogConfig.getNtcCal(idx); }
    const pullupVolt& getDigitalPullup(size_t idx) const { return m_analogConfig.getDigitalPullup(idx); };
    void setAnalogConfig(size_t idx, const analogCal& cal) { m_analogConfig.writeAnalogCal(idx) = cal; };
    void setNtcConfig(size_t idx, const ntcCal& cal) { m_analogConfig.writeNtcCal(idx) = cal; };
    void setDigitalPullup(size_t idx, pullupVolt pu) { m_analogConfig.writePullup(idx, pu); };
};

config &getConfig();

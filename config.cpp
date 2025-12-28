// config.cpp
#include "config.h"
#include "flash.h"
#include <cstring>
#include <cstdint>

pullup::pullup(ioportid_t port, iopadid_t pad) : m_port(port), m_pad(pad)
{
    palSetPadMode(m_port, m_pad, PAL_MODE_OUTPUT_PUSHPULL);
    setLow();
}


pullupsStore::pullupsStore() : m_5vpullups{ pullup(GPIOB, 5), pullup(GPIOB, 4), pullup(GPIOB, 3), pullup(GPIOB, 2) },
                             m_12vpullups{ pullup(GPIOB, 10), pullup(GPIOA, 8), pullup(GPIOA, 9), pullup(GPIOA, 10) }
{
}

void pullupsStore::setPullup(size_t idx, pullupVolt pu)
{
    switch (pu)
    {
        case pullupVolt::V5:
            m_12vpullups[idx].setLow();
            chThdSleepMilliseconds(1);
            m_5vpullups[idx].setHigh();
            break;
        case pullupVolt::V12:
            m_5vpullups[idx].setLow();
            chThdSleepMilliseconds(1);
            m_12vpullups[idx].setHigh();
            break;
        default:
            m_5vpullups[idx].setLow();
            m_12vpullups[idx].setLow();
            break;
    }
}

static pullupsStore puStore;

/* Same address you already use */
constexpr uintptr_t CFG_ADDR = 0x0801F800; // last page start
constexpr uint32_t CFG_MAGIC = 0x43464731; // 'CFG1'
constexpr uint16_t CFG_VERSION = 1;

/* Small CRC32 (standard polynomial 0xEDB88320). Good enough for config. */
static uint32_t crc32(const uint8_t *data, size_t len)
{
    uint32_t crc = 0xFFFFFFFFu;
    for (size_t i = 0; i < len; i++)
    {
        crc ^= data[i];
        for (int b = 0; b < 8; b++)
        {
            uint32_t mask = -(crc & 1u);
            crc = (crc >> 1) ^ (0xEDB88320u & mask);
        }
    }
    return ~crc;
}

static const ConfigFlashImage *flashImage()
{
    return reinterpret_cast<const ConfigFlashImage *>(CFG_ADDR);
}

configAnalog::configAnalog()
{
    for (auto &cal : m_analogCals)
    {
        cal.factor = scaling::x1;
        cal.highCal = 300U;
        cal.highV = 4650U;
        cal.lowCal = 20U;
        cal.lowV = 500U;
    }
    for (auto &ntcCal : m_ntcCals)
    {
        ntcCal.r1 = 32000U;
        ntcCal.r2 = 16000U;
        ntcCal.r3 = 2000U;
        ntcCal.t1 = -40;
        ntcCal.t2 = 18;
        ntcCal.t3 = 70;
    }
    for (auto &pu : m_digitalPullups)
    {
        pu = pullupVolt::None;
    }
}

bool config::isFlashValid() const
{
    const ConfigFlashImage *img = flashImage();

    if (img->magic != CFG_MAGIC)
        return false;
    if (img->version != CFG_VERSION)
        return false;
    if (img->size != sizeof(configAnalog))
        return false;

    const uint32_t calc = crc32(reinterpret_cast<const uint8_t *>(&img->analog), sizeof(configAnalog));
    return (calc == img->crc);
}

void config::writeImageToFlash(const configAnalog &cfg)
{
    ConfigFlashImage img{};
    img.magic = CFG_MAGIC;
    img.version = CFG_VERSION;
    img.size = sizeof(configAnalog);
    img.analog = cfg;
    img.crc = crc32(reinterpret_cast<const uint8_t *>(&img.analog), sizeof(configAnalog));

    Flash::ErasePage(63);
    Flash::Write(CFG_ADDR, reinterpret_cast<uint8_t *>(&img), sizeof(img));

    for (size_t i = 0; i < 4; i++)
    {
        puStore.setPullup(i, m_analogConfig.getDigitalPullup(i));
    }
}

void config::loadConfigFromFlash()
{
    // only call this if isFlashValid() is true
    m_analogConfig = flashImage()->analog;
    for (size_t i = 0; i < 4; i++)
    {
        puStore.setPullup(i, m_analogConfig.getDigitalPullup(i));
    }
}

void config::save()
{
    writeImageToFlash(m_analogConfig);
    // re-read from flash if you want to be 100% sure it matches:
    // loadConfigFromFlash();
}

void config::factoryReset()
{
    configAnalog defaults; // ctor sets your defaults
    m_analogConfig = defaults;
    writeImageToFlash(m_analogConfig);
}

config::config()
{
    if (isFlashValid())
    {
        loadConfigFromFlash();
    }
    else
    {
        // FIRST BOOT AFTER PROGRAMMING (or after struct/version change):
        // create defaults in RAM and persist ONCE
        configAnalog defaults;
        m_analogConfig = defaults;
        writeImageToFlash(m_analogConfig);
    }
}

config &getConfig()
{
    static config instance;
    return instance;
}

#include "config.h"
#include "flash.h"

constexpr uintptr_t addr = 0x0801F800;

void config::loadDefault()
{
    Flash::ErasePage(63);
    m_analogConfig = {};
    Flash::Write(addr, reinterpret_cast<uint8_t*>(&m_analogConfig), sizeof(configAnalog));
}

void config::loadConfigFromFlash()
{
    m_analogConfig = *reinterpret_cast<configAnalog*>(addr);
}

void config::saveConfigToflash(uint8_t* buffer)
{
    Flash::ErasePage(63);
    Flash::Write(addr, buffer, sizeof(configAnalog));
    m_analogConfig = *reinterpret_cast<configAnalog*>(addr);
}

config::config()
{
    loadConfigFromFlash();
}

config &getConfig()
{
    static config instance;
    return instance;
}
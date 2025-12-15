#pragma once
#include "ch.h"
#include "hal.h"
#include <array>
#include "io.h"

enum class apicommand : uint8_t
{
    getData = 0xAA,
    getCals = 0xBB,
    writeCals = 0xCC
};

enum class apiresponse : uint8_t
{
    dataResponse = 0x11,
    voltsResponse = 0x22,
    avCalsResponse = 0x33,
    avCalsVoltResponse = 0x44,
    ntcCalsResponse = 0x55,
    ntcCalsTempResponse = 0x66,
    factorResponse = 0x77
};

class api
{
private:
    std::array<uint8_t, 21> m_outVolts;
    std::array<uint8_t, 21> m_outVals;
    std::array<uint8_t, 25> m_avCals;
    std::array<uint8_t, 25> m_avCalsVolt;
    std::array<uint8_t, 49> m_ntcCals;
    std::array<uint8_t, 25> m_ntcCalsTemp;
    std::array<uint8_t, 7> m_factors;
    std::array<uint8_t, 25 + 25 + 49 + 25 + 7> m_calsBuffer;
public:
    api();
    void getData();
    void getCals();
    void sendData();
    void sendCals();
    void writeCals();
};
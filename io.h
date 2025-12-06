#pragma once
#include "hal.h"
#include "ch.h"
#include <array>

class digitalInput
{
private:
    bool m_state;
    ioportid_t m_port;
    iopadid_t m_pad;

public:
    digitalInput(ioportid_t port, iopadid_t pad);
    void checkState() { m_state = palReadPad(m_port, m_pad); }
    bool getState() const { return m_state; }
};

class analogInput
{
private:
    ioportid_t m_port;
    iopadid_t m_pad;
    uint16_t m_value;

public:
    analogInput(ioportid_t port, iopadid_t pad);
    void setValue(uint16_t value) { m_value = value; }
    uint16_t getValue() const { return m_value; }
};

class analogTempInput
{
private:
    ioportid_t m_port;
    iopadid_t m_pad;
    uint16_t m_value;

public:
    analogTempInput(ioportid_t port, iopadid_t pad);
    void setValue(uint16_t value) { m_value = value; }
    uint16_t getValue() const { return m_value; }
};

class output
{
private:
    bool m_state;
    ioportid_t m_port;
    iopadid_t m_pad;
    uint8_t m_currentDc;
    bool m_isPwm;
    uint8_t m_channel;

public:
    output(ioportid_t port, iopadid_t pad, uint8_t channel);
    void toggleOutput(bool state);
    void enablePwm(bool enable)
    {
        if (m_channel != 0)
            m_isPwm = enable;
    }
    void setPwmDc(uint8_t dc) { m_currentDc = dc; }
};

class inputs
{
private:
    std::array<digitalInput, 4> m_digitalInputs;
    std::array<analogInput, 6> m_analogInputs;
    std::array<analogTempInput, 4> m_analogTempInputs;
    std::array<output, 4> m_outputs;

public:
    inputs();
    void setAnalogInputValue(uint8_t index, uint16_t value) { m_analogInputs[index].setValue(value); }
    void setAnalogTempInputValue(uint8_t index, uint16_t value) { m_analogTempInputs[index].setValue(value); }
    uint16_t getAnalogInputValue(uint8_t index) const { return m_analogInputs[index].getValue(); }
    uint16_t getAnalogTempInputValue(uint8_t index) const { return m_analogTempInputs[index].getValue(); }
    void setOutputDc(uint8_t index, uint8_t dc) { m_outputs[index].setPwmDc(dc); }
    void enableOutputPwm(uint8_t index, bool enable) { m_outputs[index].enablePwm(enable); }
    void toggleOutput(uint8_t index, bool state) { m_outputs[index].toggleOutput(state); }
    bool getDigitalInputState(uint8_t index) const { return m_digitalInputs[index].getState(); }
    void checkDigitalStates();
};

inputs &getInputs();

inline constexpr PWMConfig pwmcfg = {
    .frequency = STM32_SYSCLK,
    .period = 1200000,
    .callback = nullptr,
    .channels = {
        {PWM_OUTPUT_DISABLED, nullptr},
        {PWM_OUTPUT_ACTIVE_HIGH | PWM_COMPLEMENTARY_OUTPUT_ACTIVE_LOW, nullptr},
        {PWM_OUTPUT_ACTIVE_HIGH | PWM_COMPLEMENTARY_OUTPUT_ACTIVE_LOW, nullptr},
        {PWM_OUTPUT_ACTIVE_HIGH | PWM_COMPLEMENTARY_OUTPUT_ACTIVE_LOW, nullptr}},
    .cr2 = 0,
    .bdtr = 0,
    .dier = 0};
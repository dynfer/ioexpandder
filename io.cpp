#include "io.h"

// digitalInput
digitalInput::digitalInput(ioportid_t port, iopadid_t pad)
{
    m_port = port;
    m_pad = pad;
    m_state = true;
    palSetPadMode(m_port, m_pad, PAL_MODE_INPUT);
}

// analogInput
analogInput::analogInput(ioportid_t port, iopadid_t pad)
{
    m_port = port;
    m_pad = pad;
    m_value = 0;
    palSetPadMode(m_port, m_pad, PAL_MODE_INPUT_ANALOG);
}

// analogTempInput
analogTempInput::analogTempInput(ioportid_t port, iopadid_t pad)
{
    m_port = port;
    m_pad = pad;
    m_value = 0;
    palSetPadMode(m_port, m_pad, PAL_MODE_INPUT_ANALOG);
}

// output
output::output(ioportid_t port, iopadid_t pad, uint8_t channel)
{
    m_state = false;
    m_port = port;
    m_pad = pad;
    m_currentDc = 0;
    m_isPwm = false;
    m_channel = channel;
    if (m_channel != 0)
    {
        palSetPadMode(m_port, m_pad, PAL_MODE_ALTERNATE(2));
    }
    else
    {
        palSetPadMode(m_port, m_pad, PAL_MODE_OUTPUT_PUSHPULL);
    }
}

void output::toggleOutput(bool state)
{
    static bool init = false;
    if (!init)
    {
        pwmStart(&PWMD1, &pwmcfg);
        init = true;
    }
    if (m_isPwm && m_channel != 0)
    {
        m_state = state;
        if (m_state)
        {
            pwmEnableChannel(&PWMD1, m_channel, PWM_PERCENTAGE_TO_WIDTH(&PWMD1, static_cast<uint16_t>(m_currentDc) * 100));
        }
        else
        {
            pwmEnableChannel(&PWMD1, m_channel, 0);
        }
    }
    else
    {
        if (m_channel != 0)
        {
            m_state = state;
            if (m_state)
            {
                pwmEnableChannel(&PWMD1, m_channel, 10000);
            }
            else
            {
                pwmEnableChannel(&PWMD1, m_channel, 0);
            }
        }
        else
        {
            m_state = state;
            if (m_state)
            {
                palSetPad(m_port, m_pad);
            }
            else
            {
                palClearPad(m_port, m_pad);
            }
        }
    }
}

// inputs
inputs::inputs()
    : m_digitalInputs{{{GPIOB, 7}, {GPIOC, 13}, {GPIOC, 14}, {GPIOC, 15}}},
      m_analogInputs{{{GPIOA, 0}, {GPIOA, 1}, {GPIOA, 2}, {GPIOA, 3}, {GPIOA, 4}, {GPIOA, 5}}},
      m_analogTempInputs{{{GPIOA, 6}, {GPIOA, 7}, {GPIOB, 0}, {GPIOB, 1}}},
      m_outputs{{{GPIOB, 15, 0}, {GPIOB, 14, 1}, {GPIOB, 13, 2}, {GPIOB, 12, 3}}}
{
}

void inputs::checkDigitalStates()
{
    for (auto &dig : m_digitalInputs)
    {
        dig.checkState();
    }
}

inputs &getInputs()
{
    static inputs instance;
    return instance;
}
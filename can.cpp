#include "can.h"
#include "io.h"
#include <bitset>
#include <algorithm>

static THD_WORKING_AREA(waCanRxThread, 1024);
static void CanRxThread(void *arg)
{
    (void)arg;

    std::bitset<4> outputToggles;
    CANRxFrame rxmsg = {};

    event_listener_t el_can_rx;
    chEvtRegister(&CAND1.rxfull_event, &el_can_rx, 1);

    inputs& g_inputs = getInputs();

    chRegSetThreadName("CAN RX Thread");

    while (true)
    {
        eventmask_t em = chEvtWaitAnyTimeout(EVENT_MASK(1), TIME_MS2I(10));
        if (em & EVENT_MASK(1))
        {
            while (canReceive(&CAND1, CAN_ANY_MAILBOX, &rxmsg, TIME_IMMEDIATE) == MSG_OK)
            {
                for (size_t i = 0; i < 4; i++)
                {
                    outputToggles[i] = (rxmsg.data8[0] & (1U << i)) != 0;
                    if (i != 0)
                    {
                        g_inputs.setOutputDc(i, std::clamp(rxmsg.data8[i], static_cast<uint8_t>(0), static_cast<uint8_t>(100)));
                    }
                }
                for (size_t i = 0; i < 4; i++)
                {
                    g_inputs.toggleOutput(i, outputToggles[i]);
                }
            }
        }
    }
}

static THD_WORKING_AREA(waCanTxThread, 1024);
static void CanTxThread(void *arg)
{
    (void)arg;

    chRegSetThreadName("CAN TX Thread");
    CANTxFrame txmsg1 = {};
    CANTxFrame txmsg2 = {};
    CANTxFrame txmsg3 = {};
    txmsg1.IDE = CAN_IDE_STD;
    txmsg1.RTR = CAN_RTR_DATA;
    txmsg1.SID = 0xBA;
    txmsg1.DLC = 8;
    txmsg2.IDE = CAN_IDE_STD;
    txmsg2.RTR = CAN_RTR_DATA;
    txmsg2.SID = 0xBB;
    txmsg2.DLC = 8;
    txmsg3.IDE = CAN_IDE_STD;
    txmsg3.RTR = CAN_RTR_DATA;
    txmsg3.SID = 0xBC;
    txmsg3.DLC = 8;

    inputs& g_inputs = getInputs();

    while (true)
    {
        for (size_t i = 0; i < 4; i++)
        {
            txmsg1.data16[i] = g_inputs.getAnalogTempInputValue(i);
        }
        canTransmit(&CAND1, CAN_ANY_MAILBOX, &txmsg1, TIME_IMMEDIATE);
        for (size_t i = 0; i < 6; i++)
        {
            if (i < 4)
            {
                txmsg2.data16[i] = g_inputs.getAnalogInputValue(i);
            }
            else
            {
                txmsg3.data16[i - 4] = g_inputs.getAnalogInputValue(i);
            }
            if (i == 5)
            {
                for(size_t j = 0; j < 4; j++)
                { 
                    txmsg3.data8[i - 1 + j] = g_inputs.getDigitalInputState(j);
                }
            }
        }
        canTransmit(&CAND1, CAN_ANY_MAILBOX, &txmsg2, TIME_IMMEDIATE);
        canTransmit(&CAND1, CAN_ANY_MAILBOX, &txmsg3, TIME_IMMEDIATE);
        chThdSleepMilliseconds(20);
    }
}

void startCanThreads()
{
    /* PB8 = CAN_RX (AF4) */
    palSetPadMode(GPIOB, 8U,
                  PAL_MODE_ALTERNATE(4) |
                      PAL_STM32_OTYPE_PUSHPULL |
                      PAL_STM32_OSPEED_HIGHEST);

    /* PB9 = CAN_TX (AF4) */
    palSetPadMode(GPIOB, 9U,
                  PAL_MODE_ALTERNATE(4) |
                      PAL_STM32_OTYPE_PUSHPULL |
                      PAL_STM32_OSPEED_HIGHEST);

    canSTM32SetFilters(&CAND1, 0, 1, &filter);
    canStart(&CAND1, &cancfg);
    chThdCreateStatic(waCanRxThread, sizeof(waCanRxThread), NORMALPRIO - 4, CanRxThread, nullptr);
    chThdCreateStatic(waCanTxThread, sizeof(waCanTxThread), NORMALPRIO - 2, CanTxThread, nullptr);
}
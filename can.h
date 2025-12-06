#pragma once
#include "hal.h"
#include "ch.h"

inline constexpr CANConfig cancfg =
    {
        CAN_MCR_ABOM | CAN_MCR_AWUM | CAN_MCR_TXFP,
        /*
         For 48MHz http://www.bittiming.can-wiki.info/ gives us Pre-scaler=6, Seq 1=13 and Seq 2=2. Subtract '1' for register values
        */
        CAN_BTR_SJW(0) | CAN_BTR_BRP(5) | CAN_BTR_TS1(12) | CAN_BTR_TS2(1)

};

inline constexpr CANFilter filter = {
    .filter = 0,
    .mode = 0,
    .scale = 1,
    .assignment = 0,
    .register1 = ((uint32_t)0xABU << 21),
    .register2 = ((uint32_t)0xABU << 21) | (1U << 2)
};

void startCanThreads();
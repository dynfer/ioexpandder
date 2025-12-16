#include "ch.h"
#include "hal.h"
#include "usb_config.h"
#include "usbcfg.h"
#include "io.h"
#include "api.h"

static THD_WORKING_AREA(waUsbThread, 1024);
static THD_FUNCTION(UsbThread, arg)
{

    (void)arg;
    chRegSetThreadName("USB Thread");

    static api apiInstance;

    while (true)
    {
        if (!usbIsConfigured()) {
            chThdSleepMilliseconds(50);
            continue;
        }
        /* Check if the USB is active (enumerated and configured). */
        if (usbGetDriverStateI(serusbcfg.usbp) == USB_ACTIVE)
        {
            uint8_t rx;
            if (chnReadTimeout(&SDU1, &rx, 1, TIME_INFINITE) == 1)
            {
                switch (rx)
                {
                case static_cast<uint8_t>(apicommand::getData):
                    apiInstance.getData();
                    apiInstance.sendData();
                    break;
                case static_cast<uint8_t>(apicommand::getCals):
                    apiInstance.getCals();
                    apiInstance.sendCals();
                    break;
                case static_cast<uint8_t>(apicommand::writeCals):
                    apiInstance.writeCals();
                    break;
                default:
                    break;
                }
            }
        }
    }
}

void startUsb()
{
    sduObjectInit(&SDU1);
    sduStart(&SDU1, &serusbcfg);

    usbDisconnectBus(serusbcfg.usbp);
    chThdSleepMilliseconds(1500); // Wait for host to recognize disconnect
    usbStart(serusbcfg.usbp, &usbcfg);
    usbConnectBus(serusbcfg.usbp);

    chThdCreateStatic(waUsbThread, sizeof(waUsbThread), NORMALPRIO + 2, UsbThread, NULL);
}
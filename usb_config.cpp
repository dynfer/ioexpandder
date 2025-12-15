#include "ch.h"
#include "hal.h"
#include "usb_config.h"
#include "usbcfg.h"
#include "io.h"
#include "api.h"

/*
static void writeLiteral(BaseChannel *chp, const char *s)
{
    // use sizeof(...) - 1 for literals so we don't need strlen
    while (*s) {
        chnWrite(chp, reinterpret_cast<const uint8_t *>(s), 1);
        ++s;
    }
}

static void writeU16(BaseChannel *chp, uint16_t v)
{
    char buf[5]; // 0..65535 -> max 5 digits
    uint8_t pos = 0;

    if (v == 0) {
        buf[pos++] = '0';
    } else {
        char tmp[5];
        uint8_t tpos = 0;

        while (v > 0 && tpos < sizeof(tmp)) {
            tmp[tpos++] = static_cast<char>('0' + (v % 10));
            v /= 10;
        }

        // reverse into buf
        while (tpos > 0) {
            buf[pos++] = tmp[--tpos];
        }
    }

    chnWrite(chp, reinterpret_cast<const uint8_t *>(buf), pos);
}

static void writeSpace(BaseChannel *chp)
{
    const char c = ' ';
    chnWrite(chp, reinterpret_cast<const uint8_t *>(&c), 1);
}

static void writeNewline(BaseChannel *chp)
{
    const char crlf[2] = {'\r', '\n'};
    chnWrite(chp, reinterpret_cast<const uint8_t *>(crlf), 2);
}

static void UsbPrintInputs(void)
{
    BaseChannel *chp = reinterpret_cast<BaseChannel *>(&SDU1);
    inputs &g_inputs = getInputs();

    // Analog inputs (PA0..PA5)
    writeLiteral(chp, "AI: ");
    for (uint8_t i = 0; i < 6; ++i) {
        writeU16(chp, g_inputs.getAnalogInputValue(i));
        writeSpace(chp);
    }
    writeNewline(chp);

    writeLiteral(chp, "AIV: ");
    for (uint8_t i = 0; i < 6; i++)
    {
        writeU16(chp, g_inputs.getAnalogVolt(i));
        writeSpace(chp);
    }
    writeNewline(chp);

    // Temp analog inputs (PA6, PA7, PB0, PB1)
    writeLiteral(chp, "TEMP: ");
    for (uint8_t i = 0; i < 4; ++i) {
        writeU16(chp, g_inputs.getAnalogTempInputValue(i));
        writeSpace(chp);
    }
    writeNewline(chp);

    writeLiteral(chp, "ATV: ");
    for (uint8_t i = 0; i < 4; i++)
    {
        writeU16(chp, g_inputs.getAnalogTempVolt(i));
        writeSpace(chp);
    }
    writeNewline(chp);

    // Digital inputs (4 lines of 0/1)
    writeLiteral(chp, "DIN: ");
    for (uint8_t i = 0; i < 4; ++i) {
        char c = g_inputs.getDigitalInputState(i) ? '1' : '0';
        chnWrite(chp, reinterpret_cast<const uint8_t *>(&c), 1);
        writeSpace(chp);
    }
    writeNewline(chp);
    writeNewline(chp);
}
*/

static THD_WORKING_AREA(waUsbThread, 256);
static THD_FUNCTION(UsbThread, arg)
{

    (void)arg;
    chRegSetThreadName("USB Thread");

    static api apiInstance;

    while (true)
    {
        /* Check if the USB is active (enumerated and configured). */
        if (usbGetDriverStateI(serusbcfg.usbp) == USB_ACTIVE)
        {
            //UsbPrintInputs();
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
        chThdSleepMilliseconds(20);
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
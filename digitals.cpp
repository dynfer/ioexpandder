#include "digitals.h"
#include "io.h"

static THD_WORKING_AREA(waDigitalsThread, 64);
static void digitalsThread(void *arg)
{
    (void)arg;

    inputs& g_inputs = getInputs();

    while (true)
    {
        g_inputs.checkDigitalStates();
        chThdSleepMilliseconds(50);
    }
}

void startDigitals()
{
    chThdCreateStatic(waDigitalsThread, sizeof(waDigitalsThread), NORMALPRIO, digitalsThread, nullptr);
}
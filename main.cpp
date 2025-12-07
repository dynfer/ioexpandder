#include "ch.h"
#include "hal.h"
#include "io.h"
#include "can.h"
#include "analog.h"
#include "digitals.h"
#include "usb_config.h"
#include "config.h"

int main(void) {
  halInit();
  chSysInit();
  palSetPadMode(GPIOA, 15, PAL_MODE_OUTPUT_PUSHPULL);

  (void)getConfig();
  (void)getInputs();


  startAnalogSampling();
  startDigitals();
  startCanThreads();

  startUsb();

  while (true) {
    palTogglePad(GPIOA, 15);
    chThdSleepMilliseconds(100);
  }
}

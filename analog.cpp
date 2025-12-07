#include "analog.h"
#include "io.h"
#include "util.h"

constexpr uint8_t ADC_CHANNELS = 10;
constexpr uint8_t ADC_OVERSAMPLE = 80;
constexpr float VDDA = 3.3f;
constexpr float OVERSAMPLE = static_cast<float>(ADC_OVERSAMPLE);
constexpr float R_TOP = 5600.0f;
constexpr float R_BOTTOM = 10000.0f;

static adcsample_t adcBuffer[ADC_CHANNELS * ADC_OVERSAMPLE];

static chibios_rt::BinarySemaphore adcDoneSemaphore(/* taken =*/true);

static void adcDoneCallback(ADCDriver *)
{
    chSysLockFromISR();
    adcDoneSemaphore.signalI();
    chSysUnlockFromISR();
}

static constexpr ADCConversionGroup adcgrpcfg = {
    .circular = false,
    .num_channels = ADC_CHANNELS,
    .end_cb = adcDoneCallback,
    .error_cb = nullptr,
    .cfgr1 = ADC_CFGR1_CONT | ADC_CFGR1_RES_12BIT,
    .tr = ADC_TR(0, 0),
    .smpr = ADC_SMPR_SMP_239P5,
    .chselr = ADC_CHSELR_CHSEL0 | ADC_CHSELR_CHSEL1 | ADC_CHSELR_CHSEL2 | ADC_CHSELR_CHSEL3 | ADC_CHSELR_CHSEL4 | ADC_CHSELR_CHSEL5 | ADC_CHSELR_CHSEL6 | ADC_CHSELR_CHSEL7 | ADC_CHSELR_CHSEL8 | ADC_CHSELR_CHSEL9};

static float AverageSamples(adcsample_t *buffer, size_t idx)
{
    uint32_t sum = 0;

    for (size_t i = 0; i < ADC_OVERSAMPLE; i++)
    {
        sum += buffer[idx];
        idx += ADC_CHANNELS;
    }

    const float vAdc = (static_cast<float>(sum) * VDDA) / (4095.0f * ADC_OVERSAMPLE);
    const float vin5 = vAdc * (R_TOP + R_BOTTOM) / R_BOTTOM;

    return vin5;
}

static void AnalogSampleFinish()
{
    adcDoneSemaphore.wait(TIME_INFINITE);

    inputs &g_inputs = getInputs();

    for (size_t ch = 0; ch < ADC_CHANNELS; ch++)
    {
        const uint16_t value_mV = static_cast<uint16_t>(AverageSamples(adcBuffer, ch) * 1000.0f);
        switch (ch)
        {
        case 0:
            g_inputs.setAnalogInputValue(4,  getOutputValue(value_mV, ch));
            break;
        case 1:
            g_inputs.setAnalogInputValue(1,  getOutputValue(value_mV, ch));
            break;
        case 2:
            g_inputs.setAnalogInputValue(2,  getOutputValue(value_mV, ch));
            break;
        case 3:
            g_inputs.setAnalogInputValue(0,  getOutputValue(value_mV, ch));
            break;
        case 4:
            g_inputs.setAnalogInputValue(5,  getOutputValue(value_mV, ch));
            break;
        case 5:
            g_inputs.setAnalogInputValue(3,  getOutputValue(value_mV, ch));
            break;
        default:
            break;
        }
        g_inputs.setAnalogTempInputValue(ch - 6, getOutputValue(value_mV, ch, true));
    }
}

static THD_WORKING_AREA(waAnalogThread, 1024);
static void AnalogThread(void *arg)
{
    (void)arg;
    chRegSetThreadName("Analog Thread");

    adcStartConversion(&ADCD1, &adcgrpcfg, adcBuffer, ADC_OVERSAMPLE);

    while (true)
    {
        AnalogSampleFinish();
        adcStartConversion(&ADCD1, &adcgrpcfg, adcBuffer, ADC_OVERSAMPLE);
    }
}

void startAnalogSampling()
{
    adcStart(&ADCD1, nullptr);
    chThdCreateStatic(waAnalogThread, sizeof(waAnalogThread), NORMALPRIO, AnalogThread, nullptr);
}
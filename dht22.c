#define _GNU_SOURCE  // Enable GNU extensions like pthread_timedjoin_np
#include <pigpio.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <pthread.h>
#include <unistd.h>
#include <signal.h>
#include <time.h>
#include "dht22.h" 

// --- Global State for the Polling Thread (DEFINITIONS) ---
#define MAX_TIMINGS 85
#define SENSOR_POLL_INTERVAL_SEC 10 // 10 seconds
#define MAX_READ_ATTEMPTS 5 // Internal retries per poll cycle

static float g_temperature_c = 0.0f;
static float g_humidity = 0.0f;
static int g_data_available = 0; 
static pthread_mutex_t g_data_mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_t g_polling_thread;
static int g_gpioPin = -1;
static volatile int g_terminate_thread = 0;

// --- Internal Helper Function (Robust Sensor Read Logic) ---
static int dht22_read_single_attempt(int gpioPin, float *temperature, float *humidity)
{
    int data[5] = {0, 0, 0, 0, 0};
    int lastState = PI_HIGH;
    int counter = 0, j = 0, i;

    *temperature = 0;
    *humidity = 0;

    // 1. Initial Signal
    gpioSetMode(gpioPin, PI_OUTPUT);
    gpioWrite(gpioPin, PI_LOW);
    gpioDelay(20000);  // 20ms pull-down - OK to use gpioDelay for short precise delays
    gpioWrite(gpioPin, PI_HIGH);
    gpioDelay(40);
    gpioSetMode(gpioPin, PI_INPUT);

    // 2. Read the 40 bits of data
    for (i = 0; i < MAX_TIMINGS; i++)
    {
        counter = 0;
        while (gpioRead(gpioPin) == lastState)
        {
            counter++;
            gpioDelay(1); // Counter represents pulse duration in microseconds (us)
            if (counter == 255)
                break;
        }
        lastState = gpioRead(gpioPin);
        if (counter == 255)
            break;

        // Skip handshake pulses (first 4). Only process the 40 data pulses (i % 2 == 0 checks the high pulse).
        if ((i >= 4) && (i % 2 == 0))
        {
            data[j / 8] <<= 1;
            
            // THE CRITICAL FIX: Using 25us as the robust empirical threshold.
            // Short pulse (~28us) will fail this test (reads 0). Long pulse (~70us) will pass (reads 1).
            if (counter > 25) 
                data[j / 8] |= 1;
            
            j++;
        }
    }

    // 3. Checksum verification
    if ((j >= 40) &&
        (data[4] == ((data[0] + data[1] + data[2] + data[3]) & 0xFF)))
    {
        float h = ((data[0] << 8) + data[1]) / 10.0;
        float c = (((data[2] & 0x7F) << 8) + data[3]) / 10.0;
        if (data[2] & 0x80)
            c = -c;
        *humidity = h;
        *temperature = c;
        return 0; // success
    }
    else
    {
        return 1; // checksum fail
    }

    return 2; // read fail (timeout)
}

// --- Thread function that continuously polls the sensor ---
static void *dht22_polling_thread(void *arg)
{
    float temp_c, hum;
    sigset_t set;
    
    // Block all signals in this thread - let main thread handle them
    sigfillset(&set);
    pthread_sigmask(SIG_BLOCK, &set, NULL);

    while (!g_terminate_thread)
    {
        // Attempt up to MAX_READ_ATTEMPTS times per poll cycle
        int success = 0;
        for (int attempt = 0; attempt < MAX_READ_ATTEMPTS && !g_terminate_thread; attempt++)
        {
            int result = dht22_read_single_attempt(g_gpioPin, &temp_c, &hum);

            if (result == 0)
            {
                // Success: Update global state and break internal retry loop
                pthread_mutex_lock(&g_data_mutex);
                g_temperature_c = temp_c;
                g_humidity = hum;
                g_data_available = 1;
                pthread_mutex_unlock(&g_data_mutex);
                
                // Only print once per successful poll cycle, not on every retry
                if (!success) {
                    printf("DHT22: %.1f°C, %.1f%%\n", temp_c, hum);
                }
                success = 1;
                break;
            }
            
            // Small delay between retries - use usleep instead of gpioDelay for interruptibility
            if (attempt < MAX_READ_ATTEMPTS - 1 && !g_terminate_thread) {
                usleep(200000); // 200ms delay between internal retries
            }
        }

        // Wait for the main poll interval using sleep() instead of gpioDelay()
        // This is interruptible and won't cause signal handler issues
        for (int i = 0; i < SENSOR_POLL_INTERVAL_SEC && !g_terminate_thread; i++) {
            sleep(1); // Sleep 1 second at a time so we can check termination flag
        }
    }

    return NULL;
}

// --- Public API Functions (Implementations) ---

int dht22_start_polling(int gpioPin)
{
    if (g_gpioPin != -1) {
        printf("DHT22 polling already started\n");
        return 0; // Already started
    }

    g_gpioPin = gpioPin;
    g_terminate_thread = 0;

    if (pthread_create(&g_polling_thread, NULL, dht22_polling_thread, NULL) != 0)
    {
        fprintf(stderr, "ailed to create DHT22 polling thread\n");
        g_gpioPin = -1;
        return -1; 
    }

    printf("DHT22 polling thread started on GPIO %d\n", gpioPin);
    return 0;
}

int dht22_get_last_valid_reading(float *temperature, float *humidity)
{
    if (!g_data_available)
        return 1; // No data available yet

    pthread_mutex_lock(&g_data_mutex);
    *temperature = g_temperature_c;
    *humidity = g_humidity;
    pthread_mutex_unlock(&g_data_mutex);

    return 0; 
}

int dht22_init()
{
    // Configure pigpio to be more tolerant of signals
    gpioCfgSetInternals(gpioCfgGetInternals() | PI_CFG_NOSIGHANDLER);
    
    if (gpioInitialise() < 0)
    {
        fprintf(stderr, "Failed to initialize pigpio\n");
        return -1;
    }
    
    printf("pigpio initialized\n");
    return 0;
}

void dht22_terminate()
{
    if (g_gpioPin != -1)
    {
        printf("Terminating DHT22 polling thread...\n");
        g_terminate_thread = 1;
        
        // Wait up to 15 seconds for thread to finish
        struct timespec timeout;
        clock_gettime(CLOCK_REALTIME, &timeout);
        timeout.tv_sec += 15;
        
        int result = pthread_timedjoin_np(g_polling_thread, NULL, &timeout);
        if (result == 0) {
            printf("DHT22 polling thread terminated cleanly\n");
        } else {
            fprintf(stderr, "DHT22 thread did not terminate in time (result: %d)\n", result);
            pthread_cancel(g_polling_thread);
        }
        
        g_gpioPin = -1;
        g_data_available = 0;
    }
    
    gpioTerminate();
    printf("pigpio terminated\n");
}

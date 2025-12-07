/*
 * DHT22 Temperature/Humidity Sensor Reader for Thermostat Controller
 * 
 * This sketch reads DHT22 sensor data and responds to serial commands.
 * It converts temperature to Fahrenheit before sending.
 * 
 * Hardware:
 * - Arduino (Uno, Nano, Mega, etc.)
 * - DHT22 sensor connected to pin 2 (configurable below)
 * 
 * Serial Protocol:
 * - Baud rate: 9600
 * - Send 'R' or 'r' to request reading
 * - Response format: "T:72.5,H:45.0\n"
 *   - T: Temperature in Fahrenheit
 *   - H: Relative Humidity in %
 * 
 * Author: Thermostat Controller Project
 * License: MIT
 */

#include <DHT.h>

// ===== CONFIGURATION =====
#define DHTPIN 2          // DHT22 data pin
#define DHTTYPE DHT22     // Sensor type
#define SERIAL_BAUD 9600  // Must match C# config (BaudRate)

// ===== GLOBALS =====
DHT dht(DHTPIN, DHTTYPE);

// Cached sensor values
float lastTemperatureF = 0.0;
float lastHumidity = 0.0;
unsigned long lastReadTime = 0;

// Reading interval (don't read sensor too frequently)
const unsigned long READ_INTERVAL = 2000; // 2 seconds minimum between reads

void setup() {
  // Initialize serial communication
  Serial.begin(SERIAL_BAUD);
  
  // Initialize DHT sensor
  dht.begin();
  
  // Wait for sensor to stabilize
  delay(2000);
  
  // Perform initial reading
  readSensor();
  
  // Ready indicator
  Serial.println("READY");
}

void loop() {
  // Check for serial command
  if (Serial.available() > 0) {
    char cmd = Serial.read();
    
    // Clear any remaining characters in buffer
    while (Serial.available() > 0) {
      Serial.read();
    }
    
    // Handle read command (case-insensitive)
    if (cmd == 'R' || cmd == 'r') {
      // Update cached reading if enough time has passed
      unsigned long currentTime = millis();
      if (currentTime - lastReadTime >= READ_INTERVAL) {
        readSensor();
        lastReadTime = currentTime;
      }
      
      // Send cached reading (always respond immediately)
      sendReading();
    }
  }
  
  // Periodic update of cached values (every 10 seconds)
  unsigned long currentTime = millis();
  if (currentTime - lastReadTime >= 10000) {
    readSensor();
    lastReadTime = currentTime;
  }
}

void readSensor() {
  // Read humidity
  float h = dht.readHumidity();
  
  // Read temperature in Celsius
  float c = dht.readTemperature();
  
  // Check if readings are valid
  if (isnan(h) || isnan(c)) {
    // Keep last valid reading on error
    return;
  }
  
  // Convert Celsius to Fahrenheit
  float f = (c * 9.0 / 5.0) + 32.0;
  
  // Update cached values
  lastTemperatureF = f;
  lastHumidity = h;
}

void sendReading() {
  // Format: "T:72.5,H:45.0"
  // Temperature in Fahrenheit, Humidity in %
  
  Serial.print("T:");
  Serial.print(lastTemperatureF, 1);  // 1 decimal place
  Serial.print(",H:");
  Serial.print(lastHumidity, 1);      // 1 decimal place
  Serial.println();  // Newline terminator
}

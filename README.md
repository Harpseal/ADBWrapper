A sample utility to control your android phone only using ADB.

* Pros and cons of ADB only:
  * [Pros] **No extra APK/JAR installation** is needed.
  * [Pros] Realtime preview (by streaming in h264 with screenrecord)

* Touch simulation with mouse
  * Mode 1: ```adb shell input``` <br>
    Enable by default. Only support simulating **tap** and **swipe**

  * Mode 2: ```adb shell sendevent``` <br>
    Disable by default. To enable this mode, check the input device by ```adb shell getevent -p``` and set the correct touch dev in the context menu of power button.

![Screenshot](https://raw.githubusercontent.com/Harpseal/ADBWrapper/master/ADBWrapper/resource/screenshot/Screenshot_01.png)

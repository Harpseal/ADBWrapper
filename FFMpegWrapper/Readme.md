## Prepare FFMpeg libraries
* libavcodec.a
* libavutil.a
* libswscale.a

### Option1. Download prebuild binaries
* Shared library (Tested version: 4.2.2)

    https://ffmpeg.zeranoe.com/builds/

* Static library (Tested version: 3.4.2)

    https://github.com/FutaAlice/ffmpeg-static-libs/releases

### Option2. Build x64 library from source with WSL (Windows Subsystem for Linux)
#### Step1. Download source code (Tested version: 4.2.2)

    https://www.ffmpeg.org/

#### Step2. Install the required packages

```bash
sudo apt install gcc-mingw-w64-x86-64
```

#### Step3. Configure ffmpeg (x64)

Build h264 only to reduse the binary size.

* Shared library

    ```bash
    ./configure --arch=x86_64 --target-os=mingw64 --cross-prefix=x86_64-w64-mingw32- --enable-shared --disable-programs --disable-everything --enable-decoder=h264 --enable-parser=h264  --enable-demuxer=h264 --enable-hwaccel=h264_d3d11va --enable-hwaccel=h264_d3d11va2 --enable-hwaccel=h264_dxva2 --prefix=`pwd`/../lib --disable-debug --extra-cflags="-m64"
    ```

* Static library

    ```bash
    ./configure --arch=x86_64 --target-os=mingw64 --cross-prefix=x86_64-w64-mingw32- --enable-static --disable-shared --disable-programs --disable-everything --enable-decoder=h264 --enable-parser=h264 --enable-demuxer=h264 --enable-hwaccel=h264_d3d11va --enable-hwaccel=h264_d3d11va2 --enable-hwaccel=h264_dxva2 --prefix=`pwd`/../lib --disable-debug --extra-cflags="-m64"
    ```
    Copy the dependent static libraries from WSL

    * /usr/x86_64-w64-mingw32/lib/
      * libmingwex.a
      * libmingw32.a
      * libmsvcrt.a
      * libbcrypt.a
    * /usr/lib/gcc/x86_64-w64-mingw32/7.3-win32/
      * libgcc.a
    
    Enable appending mingw32 libraries to the linker's additional dependencies list.
    ```cpp
    //ADBWrapper\FFMpegWrapper\decoderCAPI.cpp
    #define ENABLE_MINGW32_STATIC_LIB 1
    ```

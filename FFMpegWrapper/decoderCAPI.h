#ifndef DECODER_CAPI
#define DECODER_CAPI

#include <msclr\marshal.h>
#include <msclr\marshal_cppstd.h>
#include <vcclr.h>

typedef unsigned char byte;

//#define DLL_EXPORTS extern "C" __declspec(dllexport)
//
//DLL_EXPORTS void* createDecoder(int codec_idx = 27, int buffer_size = 4096); //AV_CODEC_ID_H264
//DLL_EXPORTS void releaseDecoder(void* pDecoder);
//
//DLL_EXPORTS int decoderBuffer(void* pDecoder, byte* pRawdata, int nRawdata, int* pOutwidth, int* pOutheight, int* pOutchannels, byte* pImgdata);


class FFMpegDecoder;
public ref class FFMpegWrapperCLI
{
public:
    FFMpegWrapperCLI();
    ~FFMpegWrapperCLI();

    bool init(int codec_idx, int buffer_size, bool is_bgr);
    int decoderBuffer(System::IntPtr pRawdata, int nRawdata, System::IntPtr pOutwidth, System::IntPtr pOutheight, System::IntPtr pOutchannels, System::IntPtr pImgdata);

    //0 : decode time (Sec)
    //1 : cvtFmt time (Sec)
    //2 : mp4 record time (Sec)
    //3 : mp4 record size (KB)
    //4 : stream bit-rate IIR (KB/Sec)
    //5 : FPS (Frame/Sec)
    double getPerformance(int type);

    bool startRecMP4(System::String^ filePath);
    void stopRecMP4();
    bool isRecMP4();

private:

    FFMpegDecoder* m_pDecoder;
};
#define DECODE_STATUS_ERROR             0
#define DECODE_STATUS_OK                1
#define DECODE_STATUS_NO_UPDATE         2
#define DECODE_STATUS_SKIP_FRAME        3
#define DECODE_STATUS_UNSUPPORTED_PXFMT 4
 
#endif

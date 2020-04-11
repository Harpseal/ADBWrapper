
#if _ENABLE_UT
#include "decoderCAPI.h"
#include <stdio.h>
#include <stdlib.h>
#include <string>

static bool pgm_save(unsigned char* buf, int stride, int xsize, int ysize, int channels,
    char* filename)
{
    if (channels != 3 && channels != 1) return false;
    FILE* f;
    int i;

    if (stride < xsize * channels)
        stride = xsize * channels;

    f = fopen(filename, "wb");
    if (f == nullptr) return false;
    fprintf(f, "P%d\n%d %d\n%d\n", channels==3?6:5, xsize, ysize, 255);
    for (i = 0; i < ysize; i++)
        fwrite(buf + i * stride, 1, xsize * channels, f);
    fclose(f);
    return true;
}

#ifndef AV_INPUT_BUFFER_PADDING_SIZE
#define AV_INPUT_BUFFER_PADDING_SIZE 64
#endif
int main(int argc, char** argv)
{

    if (argc <= 2) {
        fprintf(stderr, "Usage: %s <input file> <output file>\n", argv[0]);
        return 0;
    }
    const char* filename, * outfilename;
    filename = argv[1];
    outfilename = argv[2];
    printf("filename     %s\n", filename);
    printf("outfilename  %s\n", outfilename);

    FILE* f;
    f = fopen(filename, "rb");
    if (!f) {
        fprintf(stderr, "Could not open %s\n", filename);
        exit(1);
    }

    void* pDecoder = createDecoder();

    const int nbuf = 512;
    uint8_t inbuf[nbuf];
    memset(inbuf, 0, sizeof(inbuf));

    uint8_t* data;
    size_t   data_size;
    int width = 0, height = 0, channels = 1;
    uint8_t* imgData = nullptr;
    int imgIdx = 0;

    while (!feof(f)) {
        /* read raw data from the input file */
        data_size = fread(inbuf, 1, nbuf - AV_INPUT_BUFFER_PADDING_SIZE, f);
        if (!data_size)
            break;

        /* use the parser to split the data into frames */
        data = inbuf;

        int w = width, h = height, c = channels;
        int res = decoderBuffer(pDecoder, inbuf, data_size, &w, &h, &c, imgData);
        if (w * h * c != 0)
        {
            if (w != width || h != height || c != channels)
            {
                printf("New size %d %d %d => %d %d %d\n", width, height, channels, w, h, c);
                if (imgData != nullptr)
                    delete[] imgData;
                imgData = new uint8_t[w * h * c];
                width = w;
                height = h;
                channels = c;
            }
            if (res == DECODE_STATUS_OK)
            {
                char buf[1024];
                snprintf(buf, sizeof(buf), "%s-%d.pgm", outfilename, ++imgIdx);
                if (!pgm_save(imgData,0,width,height,channels, buf))
                    printf("Error: can not save pgm %s\n", buf);
            }
        }
    }
    if (imgData != nullptr)
        delete[] imgData;


    releaseDecoder(pDecoder);
    pDecoder = nullptr;

    fclose(f);


	return 0;
}


#endif //_ENABLE_UT
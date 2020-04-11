
#include "decoderCAPI.h"
extern "C" {
#include "libavcodec/avcodec.h"
}
#include <string>

class FFMpegDecoder
{
public:
	FFMpegDecoder()
	{

	}
	~FFMpegDecoder()
	{
		release();
	}

	bool init(int codec_idx = AV_CODEC_ID_H264, int buffer_size = 4096, bool is_bgr=false)
	{
		if (!release()) return false;

		if (codec_idx < 0)
			codec_idx = AV_CODEC_ID_H264;

		m_is_bgr = is_bgr;

		m_pkt = av_packet_alloc();
		if (!m_pkt) return false;

		m_inbuf_length = buffer_size;
		m_inbuf = new uint8_t[m_inbuf_length + AV_INPUT_BUFFER_PADDING_SIZE];
		m_inbuf2 = new uint8_t[m_inbuf_length + AV_INPUT_BUFFER_PADDING_SIZE];
		m_inbuf_size = 0;

		/* set end of buffer to 0 (this ensures that no overreading happens for damaged MPEG streams) */
		//memset(m_inbuf + buffer_size, 0, AV_INPUT_BUFFER_PADDING_SIZE);
		memset(m_inbuf, 0, (m_inbuf_length + AV_INPUT_BUFFER_PADDING_SIZE)*sizeof(uint8_t));
		memset(m_inbuf2, 0, (m_inbuf_length + AV_INPUT_BUFFER_PADDING_SIZE) * sizeof(uint8_t));

		/* find the MPEG-1 video decoder */
		//include\libavcodec\avcodec.h
		m_codec = avcodec_find_decoder((AVCodecID)codec_idx);
		if (!m_codec) {
			fprintf(stderr, "Codec not found %d\n", codec_idx);
			return false;
		}

		m_parser = av_parser_init(m_codec->id);
		if (!m_parser) {
			fprintf(stderr, "parser not found\n");
			return false;
		}

		m_ctx = avcodec_alloc_context3(m_codec);
		if (!m_ctx) {
			fprintf(stderr, "Could not allocate video codec context\n");
			return false;
		}


		/* open it */
		if (avcodec_open2(m_ctx, m_codec, NULL) < 0) {
			fprintf(stderr, "Could not open codec\n");
			return false;
		}

		m_frame = av_frame_alloc();
		if (!m_frame) {
			fprintf(stderr, "Could not allocate video frame\n");
			return false;
		}
		return true;
	}

	bool release()
	{
		if (m_parser != nullptr)
			av_parser_close(m_parser);
		if (m_ctx != nullptr)
			avcodec_free_context(&m_ctx);
		if (m_frame != nullptr)
			av_frame_free(&m_frame);
		if (m_pkt != nullptr)
			av_packet_free(&m_pkt);
		if (m_inbuf != nullptr)
			delete[] m_inbuf;
		if (m_inbuf2 != nullptr)
			delete[] m_inbuf2;

		m_codec = nullptr;
		m_parser = nullptr;
		m_ctx = nullptr;
		m_frame = nullptr;
		m_inbuf = nullptr;
		m_inbuf2 = nullptr;
		m_inbuf_size = m_inbuf_length = 0;
		m_pkt = nullptr;

		return true;
	}


	int decode(byte* pRawdata, int nRawdata, int* pOutwidth, int* pOutheight, int* pOutchannels, byte* pOutImgdata)
	{
		int res_status = DECODE_STATUS_NO_UPDATE;
		bool is_update_img = false;
		while (nRawdata > 0)
		{
			int nData = nRawdata;
			if (nData + m_inbuf_size > m_inbuf_length)
				nData = m_inbuf_length - m_inbuf_size;

			memcpy(m_inbuf + m_inbuf_size, pRawdata, nData);
			m_inbuf_size += nData;

			pRawdata += nData;
			nRawdata -= nData;
			


			uint8_t* data;
			size_t   data_size;
			size_t   data_processed;
			/* use the parser to split the data into frames */
			data = m_inbuf;
			data_size = (size_t)m_inbuf_size;
			data_processed = 0;

			while (data_size > 0) {
				int ret = av_parser_parse2(m_parser, m_ctx, &m_pkt->data, &m_pkt->size,
					data, data_size, AV_NOPTS_VALUE, AV_NOPTS_VALUE, 0);
				if (ret < 0) {
					fprintf(stderr, "Error while parsing\n");
					return DECODE_STATUS_ERROR;
				}
				data += ret;
				data_processed += ret;
				data_size -= ret;

				if (m_pkt->size)
				{
					ret = avcodec_send_packet(m_ctx, m_pkt);
					if (ret < 0) {
						fprintf(stderr, "Error sending a packet for decoding\n");
						res_status = DECODE_STATUS_ERROR;
					}

					while (ret >= 0) {
						ret = avcodec_receive_frame(m_ctx, m_frame);
						if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF)
							break;
						else if (ret < 0) {
							fprintf(stderr, "Error during decoding\n");
							res_status = DECODE_STATUS_ERROR;
						}

						//printf("processing frame %3d  %d (%d x %d)\n", m_ctx->frame_number, m_frame->linesize[0], m_frame->width, m_frame->height);
						fflush(stdout);

						switch (m_frame->format)//AVPixelFormat
						{
						case AV_PIX_FMT_YUV420P:
						{
							if (is_update_img)
								break;
							int width = m_frame->width;
							int height = m_frame->height;
							int channels = *pOutchannels == 1 ? 1 : 3;

							if (*pOutwidth == width && *pOutheight == height && *pOutchannels == channels && pOutImgdata != nullptr)
							{
								is_update_img = true;
								res_status = DECODE_STATUS_OK;
								if (channels == 1)//Y only
								{
									for (int y = 0; y < height; y++)
									{
										memcpy(pOutImgdata + width * y, m_frame->data[0] + m_frame->linesize[0] * y, width);
									}
								}
								else //420 => RGB
								{
									// Y Plane(luma)
									byte* y_plane = m_frame->data[0];
									int stride = m_frame->linesize[0];

									// U Plane(chroma B - Y')
									byte* u_plane = m_frame->data[1];
									int u_stride = m_frame->linesize[1];
									
									// V Plane(chroma R - Y')
									byte* v_plane = m_frame->data[2];
									int v_stride = m_frame->linesize[2];
									
									int uvwidth = width / 2;
									int uvheight = height / 2;
									byte * rgb = pOutImgdata;

									for (int y = 0; y < height; y++)
									{
										int rowIdx = (stride * y);
										int uvpIdx = (stride / 2) * (y / 2);

										byte* pYp = y_plane + y * stride;
										byte* pUp = u_plane + (y / 2) * u_stride;
										byte* pVp = v_plane + (y / 2) * v_stride; 

										if (m_is_bgr)
										{
											for (int x = 0; x < width; x += 2)
											{
												int C1 = pYp[0] - 16;
												int C2 = pYp[1] - 16;
												int D = *pUp - 128;
												int E = *pVp - 128;

												int R1 = (298 * C1 + 409 * E + 128) >> 8;
												int G1 = (298 * C1 - 100 * D - 208 * E + 128) >> 8;
												int B1 = (298 * C1 + 516 * D + 128) >> 8;

												int R2 = (298 * C2 + 409 * E + 128) >> 8;
												int G2 = (298 * C2 - 100 * D - 208 * E + 128) >> 8;
												int B2 = (298 * C2 + 516 * D + 128) >> 8;

												rgb[2] = (byte)(R1 < 0 ? 0 : R1 > 255 ? 255 : R1);
												rgb[1] = (byte)(G1 < 0 ? 0 : G1 > 255 ? 255 : G1);
												rgb[0] = (byte)(B1 < 0 ? 0 : B1 > 255 ? 255 : B1);

												rgb[5] = (byte)(R2 < 0 ? 0 : R2 > 255 ? 255 : R2);
												rgb[4] = (byte)(G2 < 0 ? 0 : G2 > 255 ? 255 : G2);
												rgb[3] = (byte)(B2 < 0 ? 0 : B2 > 255 ? 255 : B2);

												rgb += 6;
												pYp += 2;
												pUp += 1;
												pVp += 1;
											}
										}
										else
										{
											for (int x = 0; x < width; x += 2)
											{
												int C1 = pYp[0] - 16;
												int C2 = pYp[1] - 16;
												int D = *pUp - 128;
												int E = *pVp - 128;

												int R1 = (298 * C1 + 409 * E + 128) >> 8;
												int G1 = (298 * C1 - 100 * D - 208 * E + 128) >> 8;
												int B1 = (298 * C1 + 516 * D + 128) >> 8;

												int R2 = (298 * C2 + 409 * E + 128) >> 8;
												int G2 = (298 * C2 - 100 * D - 208 * E + 128) >> 8;
												int B2 = (298 * C2 + 516 * D + 128) >> 8;

												rgb[0] = (byte)(R1 < 0 ? 0 : R1 > 255 ? 255 : R1);
												rgb[1] = (byte)(G1 < 0 ? 0 : G1 > 255 ? 255 : G1);
												rgb[2] = (byte)(B1 < 0 ? 0 : B1 > 255 ? 255 : B1);

												rgb[3] = (byte)(R2 < 0 ? 0 : R2 > 255 ? 255 : R2);
												rgb[4] = (byte)(G2 < 0 ? 0 : G2 > 255 ? 255 : G2);
												rgb[5] = (byte)(B2 < 0 ? 0 : B2 > 255 ? 255 : B2);

												rgb += 6;
												pYp += 2;
												pUp += 1;
												pVp += 1;
											}
										}
									}
								}
							}
							*pOutwidth = width;
							*pOutheight = height; 
							*pOutchannels = channels;

						}
							break;
						default:
							fprintf(stderr, "Unsupport pixel format %d\n", m_frame->format);
							res_status = DECODE_STATUS_UNSUPPORTED_PXFMT;
							ret = -1;
							break;
						}

						/* the picture is allocated by the decoder. no need to
						   free it */
						//snprintf(buf, sizeof(buf), "%s-%d.pgm", filename, dec_ctx->frame_number);
						//pgm_save(frame->data[0], frame->linesize[0],
						//	frame->width, frame->height, buf);
					}
				}
			}

			if (data_processed > m_inbuf_size)
			{
				fprintf(stderr, "Processed bytes %d > datasize %d ?!\n", data_processed, m_inbuf_size);
				return DECODE_STATUS_ERROR;
			}

			if (data_processed < m_inbuf_size)
			{
				memcpy(m_inbuf2, m_inbuf + data_processed, m_inbuf_size - data_processed);
				std::swap(m_inbuf2, m_inbuf);
			}
			m_inbuf_size -= data_processed;
		}

		if (is_update_img && res_status == DECODE_STATUS_OK)
			return DECODE_STATUS_OK;
		return res_status;
	}

private:
	const AVCodec* m_codec = nullptr;
	AVCodecParserContext* m_parser = nullptr;
	AVCodecContext* m_ctx = nullptr;
	AVFrame* m_frame = nullptr;
	uint8_t* m_inbuf = nullptr;
	uint8_t* m_inbuf2 = nullptr;
	uint32_t m_inbuf_size = 0;
	uint32_t m_inbuf_length = 0;
	AVPacket* m_pkt = nullptr;
	bool m_is_bgr = false;
}; //FFMpegDecoder


FFMpegWrapperCLI::FFMpegWrapperCLI()
{
	m_pDecoder = new FFMpegDecoder();
}

FFMpegWrapperCLI::~FFMpegWrapperCLI()
{
	delete m_pDecoder;
}

bool FFMpegWrapperCLI::init(int codec_idx, int buffer_size, bool is_bgr)
{
	return m_pDecoder->init(codec_idx, buffer_size, is_bgr);
}


int FFMpegWrapperCLI::decoderBuffer(System::IntPtr pRawdata, int nRawdata, System::IntPtr pOutwidth, System::IntPtr pOutheight, System::IntPtr pOutchannels, System::IntPtr pImgdata)
{
	return m_pDecoder->decode(reinterpret_cast<unsigned char*>(pRawdata.ToPointer()), nRawdata,
		reinterpret_cast<int*>(pOutwidth.ToPointer()),
		reinterpret_cast<int*>(pOutheight.ToPointer()),
		reinterpret_cast<int*>(pOutchannels.ToPointer()),
		pImgdata == System::IntPtr::Zero ? nullptr : reinterpret_cast<unsigned char*>(pImgdata.ToPointer())
	);
}


void* createDecoder(int codec_idx, int buffer_size)
{
	FFMpegDecoder *pDecoder = new FFMpegDecoder();
	if (pDecoder == nullptr) return nullptr;
	if (!pDecoder->init(codec_idx, buffer_size))
	{
		delete pDecoder;
		return nullptr;
	}
	return pDecoder;
}

void releaseDecoder(void* pDecoder)
{
	if (pDecoder == nullptr) return;
	FFMpegDecoder* pDecoderImp = (FFMpegDecoder*)pDecoder;
	delete pDecoderImp;
}

int decoderBuffer(void* pDecoder, byte* pRawdata, int nRawdata, int* outwidth, int* outheight, int* outchannels, byte* pImgdata)
{
	if (pDecoder == nullptr || pRawdata == nullptr) return DECODE_STATUS_ERROR;
	FFMpegDecoder* pDecoderImp = (FFMpegDecoder*)pDecoder;
	return pDecoderImp->decode(pRawdata, nRawdata, outwidth, outheight, outchannels, pImgdata);
}


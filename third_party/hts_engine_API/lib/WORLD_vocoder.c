#ifndef WORLD_VOCODER_C
#define WORLD_VOCODER_C

#ifdef __cplusplus
#define WORLD_VOCODER_C_START extern "C" {
#define WORLD_VOCODER_C_END   }
#else
#define WORLD_VOCODER_C_START
#define WORLD_VOCODER_C_END
#endif                          /* __CPLUSPLUS */

WORLD_VOCODER_C_START;

#include <math.h>               /* for sqrt(),log(),exp(),pow(),cos() */

/* hts_engine libraries */
#include "HTS_hidden.h"

#include "SPTK.h" /* for mgc2sp() */
#include "world/synthesis.h" /* for synthesis() */
#include "world/cheaptrick.h"

/* SPTK_mgc2sp: mcep to sp */
static void SPTK_mgc2sp(WORLD_Vocoder * v, double **mcep, double alpha, double gamma, size_t mcep_dim)
{
   size_t i, j;
   double *sp_buff;
   double *phase_buff;

   sp_buff= (double *) HTS_calloc(v->fft_size, sizeof(double));
   phase_buff= (double *) HTS_calloc(v->fft_size, sizeof(double));

   for (i = 0; i < v->total_frame; i++) {
      mgc2sp(mcep[i], mcep_dim - 1, alpha, 0 /* gamma */, sp_buff, phase_buff, v->fft_size);
      for (j = 0; j < v->spectrum_size; j++)
         v->spectrum_buff[i][j] = exp(sp_buff[j] * 2);
   }

   HTS_free(sp_buff);
   HTS_free(phase_buff);
}

/* WORLD_Get_FFT_Size: get FFT Size */
static int WORLD_Get_FFT_Size(WORLD_Vocoder * v)
{
   CheapTrickOption option = { 0 };
   InitializeCheapTrickOption(v->rate, &option);

   return option.fft_size;
}

/* WORLD_Vocoder_initialize: initialize vocoder */
void WORLD_Vocoder_initialize(WORLD_Vocoder * v, size_t total_frame, size_t rate, double fperiod)
{
   size_t i;
   /* set parameter */
   v->fprd = fperiod / rate * 1000.0;
   v->rate = rate;
   v->fft_size = WORLD_Get_FFT_Size(v);
   v->total_frame = total_frame;
   // waveform length ref: https://github.com/mmorise/World/blob/master/test/test.cpp
   v->wave_length = (size_t)((v->total_frame - 1) * v->fprd / 1000.0 * v->rate) + 1;
   /* init buffer */
   v->f0_buff = (double *) HTS_calloc(total_frame, sizeof(double));
   v->f0_size = total_frame;
   v->spectrum_buff = (double **) HTS_calloc(total_frame, sizeof(double *));
   for (i = 0; i < total_frame; i++)
      v->spectrum_buff[i] = (double *) HTS_calloc(v->fft_size / 2 + 1, sizeof(double));
   v->spectrum_size = v->fft_size / 2 + 1;
   v->aperiodicity_buff = (double **) HTS_calloc(total_frame, sizeof(double *));
   for (i = 0; i < total_frame; i++)
      v->aperiodicity_buff[i] = (double *) HTS_calloc(v->fft_size / 2 + 1, sizeof(double));
   v->aperiodicity_size = v->fft_size / 2 + 1;
}

/* WORLD_Vocoder_synthesize: WORLD based waveform synthesis */
void WORLD_Vocoder_synthesize(WORLD_Vocoder * v, double **lf0, double **mcep, size_t mcep_dim, double alpha, double gamma, double **five_band_aperiodicity, double *rawdata, HTS_Audio * audio)
{
   size_t i, j, wav_length;

   /* lf0 -> f0 */
   for (i = 0; i < v->f0_size; i++)
      v->f0_buff[i] = lf0[i][0] > 0 ? exp(lf0[i][0]) : 0.0;

   /* mcep -> sp */
   SPTK_mgc2sp(v, mcep, alpha, gamma, mcep_dim);

   /* 5-band aperiodicity -> WORLD's aperiodicity */
   for (i = 0; i < v->total_frame; i++) {
      for (j = 0; j < 64; j++)
         v->aperiodicity_buff[i][j] = five_band_aperiodicity[i][0];
      for (j = 64; j < 128; j++)
         v->aperiodicity_buff[i][j] = five_band_aperiodicity[i][1];
      for (j = 128; j < 256; j++)
         v->aperiodicity_buff[i][j] = five_band_aperiodicity[i][2];
      for (j = 256; j < 384; j++)
         v->aperiodicity_buff[i][j] = five_band_aperiodicity[i][3];
      for (j = 384; j < 513; j++)
         v->aperiodicity_buff[i][j] = five_band_aperiodicity[i][4];
   }

   Synthesis(v->f0_buff, v->f0_size, v->spectrum_buff, v->aperiodicity_buff,
      v->fft_size, v->fprd, v->rate, v->wave_length, rawdata);

   for (i = 0; i < v->wave_length; i++) {
      rawdata[i] *= 32767.0;
      if (rawdata[i] > 32767.0)
         rawdata[i] = 32767.0;
      else if (rawdata[i] < -32768.0)
         rawdata[i] = -32768.0;
   }

   /* output */
   if (audio) {
      for (i = 0; i < v->wave_length; i++)
         HTS_Audio_write(audio, (short)rawdata[i]);
   }
}

/* WORLD_Vocoder_clear: clear vocoder */
void WORLD_Vocoder_clear(WORLD_Vocoder * v)
{
   size_t i;
   if (v != NULL) {
      /* free buffer */
      if (v->f0_buff != NULL) {
         HTS_free(v->f0_buff);
         v->f0_buff = NULL;
      }
      v->f0_size = 0;
      if (v->spectrum_buff != NULL) {
         for (i = 0; i < v->total_frame; i++)
            HTS_free(v->spectrum_buff[i]);
         HTS_free(v->spectrum_buff);
         v->spectrum_buff = NULL;
      }
      v->spectrum_size = 0;
      if (v->aperiodicity_buff != NULL) {
         for (i = 0; i < v->total_frame; i++)
            HTS_free(v->aperiodicity_buff[i]);
         HTS_free(v->aperiodicity_buff);
         v->aperiodicity_buff = NULL;
      }
      v->aperiodicity_size = 0;
   }
}

WORLD_VOCODER_C_END;

#endif                          /* !WORLD_VOCODER_C */

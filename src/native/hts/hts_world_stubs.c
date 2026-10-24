#include "HTS_hidden.h"

void WORLD_Vocoder_initialize(
   WORLD_Vocoder *v, size_t total_frame, size_t rate, double fperiod)
{
   (void) v;
   (void) total_frame;
   (void) rate;
   (void) fperiod;
}

void WORLD_Vocoder_synthesize(
   WORLD_Vocoder *v,
   double **lf0,
   double **mcep,
   size_t mcep_dim,
   double alpha,
   double gamma,
   double **five_band_aperiodicity,
   double *rawdata,
   HTS_Audio *audio)
{
   (void) v;
   (void) lf0;
   (void) mcep;
   (void) mcep_dim;
   (void) alpha;
   (void) gamma;
   (void) five_band_aperiodicity;
   (void) rawdata;
   (void) audio;
}

void WORLD_Vocoder_clear(WORLD_Vocoder *v)
{
   (void) v;
}

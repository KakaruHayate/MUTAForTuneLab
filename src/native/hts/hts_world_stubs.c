#include <string.h>
#include "HTS_hidden.h"

void WORLD_Vocoder_initialize(
   WORLD_Vocoder *v, size_t total_frame, size_t rate, double fperiod)
{
   /* We never ask HTS for a waveform (worldline renders from the parameters), so the
      stub used to leave this struct untouched. HTS_GStreamSet_create_WORLD then read
      the uninitialised `wave_length` and passed it to HTS_calloc: most of the time the
      stack happened to be zero, but when it held a leftover value the allocation was
      huge and HTS aborted (the intermittent crash after a parameter drag). Zero the
      struct and make the waveform length explicitly zero. */
   memset(v, 0, sizeof(*v));
   v->total_frame = total_frame;
   v->rate = rate;
   v->fprd = fperiod;
   v->wave_length = 0;
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

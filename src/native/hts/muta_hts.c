/*
 * muta_hts -- the HTS engine as this plugin needs it.
 *
 * Why a new wrapper instead of the earlier instrumentation build:
 *
 *
 *   * that build kept its side state (note flags, GV suppression, capture
 *     buffers) in process-wide statics, so two sessions overwrote each other.
 *     Here every session owns its own handle, and the only ambient state is a
 *     thread-local pointer used by the MSD hook while a synthesis call is
 *     running;
 *   * it exposed layer-by-layer debug getters nobody needs, and reading one
 *     parameter cost one interop call per (frame, coefficient). Here a whole
 *     stream is copied in one call;
 *   * the capture hooks are gone: nothing here exists for diagnostics.
 *
 * The two upstream patches this depends on live in
 * third_party/hts_engine_API/patches and are applied at build time:
 *   - gstream: validate the last stream (the LPF) instead of stream 2, which in
 *     MUTA's four-stream layout is BAP;
 *   - pstream: call back into muta_hts_msd_apply so the per-note MSD rule can
 *     override the model's own split.
 */

#include <stdlib.h>
#include <string.h>

// HTS_hidden.h, not HTS_engine.h: the per-stream table (and therefore a stream's
// vector length) is internal to the engine and only visible there.
#include "HTS_hidden.h"

#if defined(_MSC_VER)
#define MUTA_TLS __declspec(thread)
#define MUTA_EXPORT __declspec(dllexport)
#else
#define MUTA_TLS __thread
#define MUTA_EXPORT __attribute__((visibility("default")))
#endif

#define MUTA_MAX_NOTES 512

typedef struct MutaHts {
    HTS_Engine engine;
    int note_flags[MUTA_MAX_NOTES];
    int note_flag_count;
    int states_per_note;
} MutaHts;

/* Set for the duration of one synthesis call, so the pstream patch can find the
   handle that owns the flags.  One synthesis per thread at a time, which is how
   every caller uses this. */
static MUTA_TLS MutaHts *g_synthesizing = NULL;

/* ---- MSD hook, called from the patched HTS_pstream.c -------------------- */

MUTA_EXPORT int muta_hts_msd_active(void)
{
    MutaHts *handle = g_synthesizing;
    return handle != NULL && handle->note_flag_count > 0 && handle->states_per_note > 0;
}

MUTA_EXPORT void muta_hts_msd_apply(HTS_SStreamSet *sss, size_t stream,
                                    HTS_Boolean *flag, double threshold,
                                    size_t max_frames, size_t *length_out)
{
    MutaHts *handle = g_synthesizing;
    size_t states, spn, state, sub, j, frame = 0, voiced_frames = 0;

    if (handle == NULL || flag == NULL)
        return;
    states = HTS_SStreamSet_get_total_state(sss);
    spn = (size_t) handle->states_per_note;
    if (spn == 0)
        return;

    /* Walk one note at a time: a voiced note keeps every one of its frames on the
       MSD path, an unvoiced note falls back to the model's own per-state decision
       (and to all-zero for the very first note, which is how the engine behaves). */
    for (state = 0; state < states; state += spn) {
        int note = (int) (state / spn);
        int voiced = 1;
        if (note >= 0 && note < handle->note_flag_count)
            voiced = handle->note_flags[note] != 0;
        for (sub = 0; sub < spn && state + sub < states; sub++) {
            size_t duration = HTS_SStreamSet_get_duration(sss, state + sub);
            for (j = 0; j < duration && frame < max_frames; j++) {
                if (voiced) {
                    flag[frame] = 1;
                } else if (spn < state) {
                    flag[frame] = (HTS_SStreamSet_get_msd(sss, stream, state + sub) > threshold)
                                  ? 1 : 0;
                } else {
                    flag[frame] = 0;
                }
                if (flag[frame])
                    voiced_frames++;
                frame++;
            }
        }
    }
    if (length_out != NULL)
        *length_out = voiced_frames;
}

/* ---- lifecycle --------------------------------------------------------- */

MUTA_EXPORT MutaHts *muta_hts_create(const char *voice_path)
{
    MutaHts *handle;
    const char *voices[1];

    if (voice_path == NULL)
        return NULL;
    handle = (MutaHts *) calloc(1, sizeof(MutaHts));
    if (handle == NULL)
        return NULL;

    voices[0] = voice_path;
    HTS_Engine_initialize(&handle->engine);
    if (HTS_Engine_load(&handle->engine, voices, 1) != TRUE) {
        HTS_Engine_clear(&handle->engine);
        free(handle);
        return NULL;
    }
    handle->states_per_note = 0;
    handle->note_flag_count = 0;
    return handle;
}

MUTA_EXPORT void muta_hts_destroy(MutaHts *handle)
{
    if (handle == NULL)
        return;
    /* HTS_Engine_clear frees the model and the condition, but NOT the per-synthesis
       streams (gss/pss/sss) or the label list. It assumes the caller refreshed first.
       Without this, every engine we throw away leaks its last block's buffers -- with a
       fresh engine per block that is several MB per render, and malloc eventually returns
       NULL inside HTS_calloc, which memsets before checking (write through NULL). */
    HTS_Engine_refresh(&handle->engine);
    HTS_Engine_clear(&handle->engine);
    free(handle);
}

/* ---- synthesis --------------------------------------------------------- */

/* Returns the frame count, or -1 on failure. */
MUTA_EXPORT int muta_hts_synthesize(MutaHts *handle, const char *const *labels, int label_count)
{
    MutaHts *previous;
    HTS_Boolean ok;

    if (handle == NULL || labels == NULL || label_count <= 0)
        return -1;

    previous = g_synthesizing;
    g_synthesizing = handle;
    ok = HTS_Engine_synthesize_from_strings_WORLD(&handle->engine,
                                                  (char **) labels,
                                                  (size_t) label_count);
    g_synthesizing = previous;

    if (ok != TRUE)
        return -1;
    return (int) HTS_Engine_get_total_frame(&handle->engine);
}

MUTA_EXPORT void muta_hts_set_speed(MutaHts *handle, double speed)
{
    if (handle != NULL)
        HTS_Engine_set_speed(&handle->engine, speed);
}

MUTA_EXPORT void muta_hts_set_gv_weight(MutaHts *handle, int stream, double weight)
{
    if (handle != NULL)
        HTS_Engine_set_gv_weight(&handle->engine, (size_t) stream, weight);
}

MUTA_EXPORT double muta_hts_get_gv_weight(MutaHts *handle, int stream)
{
    if (handle == NULL)
        return 0.0;
    return HTS_Engine_get_gv_weight(&handle->engine, (size_t) stream);
}

MUTA_EXPORT void muta_hts_set_msd_threshold(MutaHts *handle, int stream, double value)
{
    if (handle != NULL)
        HTS_Engine_set_msd_threshold(&handle->engine, (size_t) stream, value);
}

MUTA_EXPORT void muta_hts_set_phoneme_alignment(MutaHts *handle, int enabled)
{
    if (handle != NULL)
        HTS_Engine_set_phoneme_alignment_flag(&handle->engine, enabled ? TRUE : FALSE);
}

/* notes: 1 = voiced, 0 = unvoiced, one entry per note.  Consumed by the MSD hook. */
MUTA_EXPORT int muta_hts_set_note_flags(MutaHts *handle, const int *flags, int count,
                                        int states_per_note)
{
    int i;
    if (handle == NULL || flags == NULL || count < 0 || count > MUTA_MAX_NOTES
        || states_per_note < 1)
        return -1;
    for (i = 0; i < count; i++)
        handle->note_flags[i] = flags[i];
    handle->note_flag_count = count;
    handle->states_per_note = states_per_note;
    return count;
}

/* ---- queries ----------------------------------------------------------- */

MUTA_EXPORT int muta_hts_sampling_frequency(MutaHts *handle)
{
    return handle == NULL ? 0 : (int) HTS_Engine_get_sampling_frequency(&handle->engine);
}

MUTA_EXPORT int muta_hts_frame_period(MutaHts *handle)
{
    return handle == NULL ? 0 : (int) HTS_Engine_get_fperiod(&handle->engine);
}

MUTA_EXPORT int muta_hts_num_states(MutaHts *handle)
{
    return handle == NULL ? 0 : (int) HTS_Engine_get_nstate(&handle->engine);
}

MUTA_EXPORT int muta_hts_num_streams(MutaHts *handle)
{
    return handle == NULL ? 0 : (int) HTS_Engine_get_nstream(&handle->engine);
}

MUTA_EXPORT int muta_hts_vector_length(MutaHts *handle, int stream)
{
    /* Read it off the generated (PDF) stream rather than the model set: the same
       number, through the public accessors, and only meaningful once the engine has
       synthesised -- which is the only time callers ask. */
    if (handle == NULL || stream < 0
        || (size_t) stream >= HTS_PStreamSet_get_nstream(&handle->engine.pss))
        return 0;
    return (int) HTS_PStreamSet_get_vector_length(&handle->engine.pss, (size_t) stream);
}

MUTA_EXPORT int muta_hts_total_frame(MutaHts *handle)
{
    return handle == NULL ? 0 : (int) HTS_Engine_get_total_frame(&handle->engine);
}

MUTA_EXPORT int muta_hts_total_state(MutaHts *handle)
{
    return handle == NULL ? 0 : (int) HTS_Engine_get_total_state(&handle->engine);
}

MUTA_EXPORT int muta_hts_state_duration(MutaHts *handle, int state)
{
    if (handle == NULL)
        return 0;
    return (int) HTS_Engine_get_state_duration(&handle->engine, (size_t) state);
}

/*
 * Copy one stream's generated parameters into out, laid out as
 * [frame][vector_length] (row major), total_frame rows.  Returns the number of
 * values written, or -1 on a bad argument.
 *
 * This is the reason the wrapper exists: the reference binding read one value at
 * a time, which for a phrase is tens of thousands of interop calls.
 */
MUTA_EXPORT int muta_hts_get_stream(MutaHts *handle, int stream, double *out)
{
    size_t frame, vector, total, width;

    if (handle == NULL || out == NULL)
        return -1;
    if (stream < 0 || (size_t) stream >= HTS_PStreamSet_get_nstream(&handle->engine.pss))
        return -1;
    total = HTS_Engine_get_total_frame(&handle->engine);
    width = HTS_PStreamSet_get_vector_length(&handle->engine.pss, (size_t) stream);
    if (total == 0 || width == 0)
        return 0;

    for (frame = 0; frame < total; frame++)
        for (vector = 0; vector < width; vector++)
            out[frame * width + vector] = HTS_Engine_get_generated_parameter(
                &handle->engine, (size_t) stream, frame, vector);

    return (int) (total * width);
}

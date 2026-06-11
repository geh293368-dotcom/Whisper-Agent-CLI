#include "whisper.h"

#include <algorithm>
#include <cstdint>
#include <memory>
#include <string>
#include <thread>
#define NOMINMAX
#include <windows.h>

#define WD_API extern "C" __declspec(dllexport)

using wd_progress_callback = void(__cdecl *)(int progress, void * user_data);
using wd_segment_callback = void(__cdecl *)(int64_t begin, int64_t end, const char * text, void * user_data);
using wd_cancel_callback = int(__cdecl *)(void * user_data);

struct wd_model {
    whisper_context * context = nullptr;
    std::string error;
};

struct wd_callbacks {
    wd_progress_callback progress;
    wd_segment_callback segment;
    wd_cancel_callback cancel;
    void * user_data;
};

static std::string utf8(const wchar_t * text) {
    if (text == nullptr || *text == L'\0') {
        return {};
    }

    const int size = WideCharToMultiByte(CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr);
    if (size <= 1) {
        return {};
    }

    std::string result(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text, -1, result.data(), size, nullptr, nullptr);
    result.pop_back();
    return result;
}

static void on_progress(whisper_context *, whisper_state *, int progress, void * user_data) {
    auto * callbacks = static_cast<wd_callbacks *>(user_data);
    if (callbacks->progress != nullptr) {
        callbacks->progress(progress, callbacks->user_data);
    }
}

static void on_segment(whisper_context * context, whisper_state *, int count_new, void * user_data) {
    auto * callbacks = static_cast<wd_callbacks *>(user_data);
    if (callbacks->segment == nullptr) {
        return;
    }

    const int count = whisper_full_n_segments(context);
    const int first = std::max(0, count - count_new);
    for (int index = first; index < count; ++index) {
        callbacks->segment(
            whisper_full_get_segment_t0(context, index),
            whisper_full_get_segment_t1(context, index),
            whisper_full_get_segment_text(context, index),
            callbacks->user_data);
    }
}

static bool on_abort(void * user_data) {
    auto * callbacks = static_cast<wd_callbacks *>(user_data);
    return callbacks->cancel != nullptr && callbacks->cancel(callbacks->user_data) != 0;
}

WD_API wd_model * __cdecl wd_load_model(const wchar_t * path) {
    auto model = std::make_unique<wd_model>();
    const std::string model_path = utf8(path);
    if (model_path.empty()) {
        model->error = "Model path is empty or cannot be converted to UTF-8.";
        return model.release();
    }

    whisper_context_params params = whisper_context_default_params();
    params.use_gpu = WD_USE_GPU != 0;
    model->context = whisper_init_from_file_with_params(model_path.c_str(), params);
    if (model->context == nullptr) {
        model->error = "whisper.cpp could not load the model.";
    }
    return model.release();
}

WD_API int __cdecl wd_model_ready(const wd_model * model) {
    return model != nullptr && model->context != nullptr;
}

WD_API const char * __cdecl wd_last_error(const wd_model * model) {
    return model == nullptr ? "Invalid model handle." : model->error.c_str();
}

WD_API int __cdecl wd_transcribe(
    wd_model * model,
    const float * samples,
    int sample_count,
    const char * language,
    int translate,
    wd_progress_callback progress,
    wd_segment_callback segment,
    wd_cancel_callback cancel,
    void * user_data) {
    if (model == nullptr || model->context == nullptr || samples == nullptr || sample_count <= 0) {
        return -1;
    }

    wd_callbacks callbacks{progress, segment, cancel, user_data};
    whisper_full_params params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
    params.n_threads = std::max(1u, std::min(8u, std::thread::hardware_concurrency()));
    params.language = language;
    params.translate = translate != 0;
    params.no_context = true;
    params.print_progress = false;
    params.print_realtime = false;
    params.print_timestamps = false;
    params.new_segment_callback = on_segment;
    params.new_segment_callback_user_data = &callbacks;
    params.progress_callback = on_progress;
    params.progress_callback_user_data = &callbacks;
    params.abort_callback = on_abort;
    params.abort_callback_user_data = &callbacks;

    const int result = whisper_full(model->context, params, samples, sample_count);
    if (result != 0) {
        model->error = cancel != nullptr && cancel(user_data) != 0
            ? "Transcription was cancelled."
            : "whisper_full failed with code " + std::to_string(result) + ".";
    } else {
        model->error.clear();
    }
    return result;
}

WD_API int __cdecl wd_segment_count(const wd_model * model) {
    return model != nullptr && model->context != nullptr
        ? whisper_full_n_segments(model->context)
        : 0;
}

WD_API int64_t __cdecl wd_segment_begin(const wd_model * model, int index) {
    return whisper_full_get_segment_t0(model->context, index);
}

WD_API int64_t __cdecl wd_segment_end(const wd_model * model, int index) {
    return whisper_full_get_segment_t1(model->context, index);
}

WD_API const char * __cdecl wd_segment_text(const wd_model * model, int index) {
    return whisper_full_get_segment_text(model->context, index);
}

WD_API void __cdecl wd_free_model(wd_model * model) {
    if (model == nullptr) {
        return;
    }
    whisper_free(model->context);
    delete model;
}

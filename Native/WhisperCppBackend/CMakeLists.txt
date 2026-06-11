cmake_minimum_required(VERSION 3.20)
project(WhisperCppBackend LANGUAGES C CXX)

option(WD_ENABLE_CUDA "Build the CUDA backend" OFF)
set(WD_OUTPUT_NAME "WhisperCppBackendCpu" CACHE STRING "Backend DLL name")

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(GGML_NATIVE OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
set(GGML_CCACHE OFF CACHE BOOL "" FORCE)
set(GGML_CUDA ${WD_ENABLE_CUDA} CACHE BOOL "" FORCE)
set(GGML_CUDA_NCCL OFF CACHE BOOL "" FORCE)

add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/../../ThirdParty/whisper.cpp whisper.cpp)

add_library(WhisperCppBackend SHARED WhisperCppBackend.cpp)
target_compile_features(WhisperCppBackend PRIVATE cxx_std_17)
target_compile_definitions(WhisperCppBackend PRIVATE WD_USE_GPU=$<BOOL:${WD_ENABLE_CUDA}>)
target_link_libraries(WhisperCppBackend PRIVATE whisper)
target_include_directories(WhisperCppBackend PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../../ThirdParty/whisper.cpp/include)

set_target_properties(WhisperCppBackend PROPERTIES
    OUTPUT_NAME ${WD_OUTPUT_NAME}
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin
    LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)

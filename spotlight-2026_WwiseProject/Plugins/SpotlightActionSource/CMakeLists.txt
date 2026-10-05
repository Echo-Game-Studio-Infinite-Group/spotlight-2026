cmake_minimum_required(VERSION 3.20)

project(SpotlightActionSource LANGUAGES CXX)

if(NOT DEFINED WWISE_SDK)
    set(WWISE_SDK "D:/Wwise_2024.1.17.9170/SDK" CACHE PATH "Wwise SDK root")
endif()

if(NOT EXISTS "${WWISE_SDK}/include/AK/SoundEngine/Common/IAkPlugin.h")
    message(FATAL_ERROR "Wwise SDK not found at ${WWISE_SDK}")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

set(COMMON_DEFINITIONS
    AK_WIN
    WIN64
    NDEBUG
    AK_OPTIMIZED
    NOMINMAX
    _CRT_SECURE_NO_WARNINGS
)

set(COMMON_OPTIONS
    /utf-8
    /Zc:inline
    /W3
)

add_library(SpotlightActionSource STATIC
    Runtime/PcmVoiceBridge.cpp
    Runtime/SpotlightActionSourcePlugin.cpp
    Runtime/SpotlightActionSourceParams.cpp
)

target_include_directories(SpotlightActionSource
    PUBLIC "${WWISE_SDK}/include"
)

target_compile_definitions(SpotlightActionSource PRIVATE ${COMMON_DEFINITIONS})
target_compile_options(SpotlightActionSource PRIVATE ${COMMON_OPTIONS})
set_target_properties(SpotlightActionSource PROPERTIES
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib/$<CONFIG>/static"
)

add_library(SpotlightActionDsp STATIC
    Runtime/Dsp/SpotlightActionDsp.cpp
)

target_include_directories(SpotlightActionDsp
    PUBLIC "${WWISE_SDK}/include"
)

target_compile_definitions(SpotlightActionDsp PRIVATE ${COMMON_DEFINITIONS})
target_compile_options(SpotlightActionDsp PRIVATE ${COMMON_OPTIONS})
set_target_properties(SpotlightActionDsp PROPERTIES
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib/$<CONFIG>/dsp"
)

add_library(SpotlightActionSourcePlugin SHARED
    Dynamic/InitPlugins.cpp
)

target_link_libraries(SpotlightActionSourcePlugin PRIVATE SpotlightActionSource)
target_compile_definitions(SpotlightActionSourcePlugin PRIVATE ${COMMON_DEFINITIONS})
target_compile_options(SpotlightActionSourcePlugin PRIVATE ${COMMON_OPTIONS})
set_target_properties(SpotlightActionSourcePlugin PROPERTIES
    PREFIX ""
    OUTPUT_NAME "SpotlightActionSource"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/$<CONFIG>/runtime"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib/$<CONFIG>/runtime"
)

add_library(SpotlightActionSourceAuthoring SHARED
    Authoring/SpotlightActionSourceAuthoring.cpp
)

target_link_libraries(SpotlightActionSourceAuthoring PRIVATE SpotlightActionSource)
target_compile_definitions(SpotlightActionSourceAuthoring PRIVATE
    ${COMMON_DEFINITIONS}
    _WINDOWS
    _USRDLL
    _WINDLL
    _UNICODE
    UNICODE
    WWISE_AUTHORING
    AUDIOKINETIC
    _SECURE_SCL=0
)
target_compile_options(SpotlightActionSourceAuthoring PRIVATE ${COMMON_OPTIONS})
set_target_properties(SpotlightActionSourceAuthoring PROPERTIES
    PREFIX ""
    OUTPUT_NAME "SpotlightActionSource"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/$<CONFIG>/authoring"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib/$<CONFIG>/authoring"
)

add_custom_command(TARGET SpotlightActionSourceAuthoring POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_CURRENT_SOURCE_DIR}/Authoring/SpotlightActionSource.xml"
        "$<TARGET_FILE_DIR:SpotlightActionSourceAuthoring>/SpotlightActionSource.xml"
)

add_executable(SpotlightActionDspTests
    Tests/SpotlightActionDspTests.cpp
)

target_link_libraries(SpotlightActionDspTests PRIVATE SpotlightActionDsp)
target_compile_definitions(SpotlightActionDspTests PRIVATE ${COMMON_DEFINITIONS})
target_compile_options(SpotlightActionDspTests PRIVATE ${COMMON_OPTIONS})
set_target_properties(SpotlightActionDspTests PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/$<CONFIG>/tests"
)

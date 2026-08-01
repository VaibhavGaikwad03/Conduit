cmake_minimum_required(VERSION 3.20)
project(ConduitCamera LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# The virtual-camera source is a COM in-proc server DLL that Windows' Frame Server
# loads into consuming apps. It must be 64-bit and link the Media Foundation stack.
add_library(ConduitCamera SHARED
    dllmain.cpp
    ClassFactory.cpp
    MediaSource.cpp
    SourceActivate.cpp
    MediaStream.cpp
    Control.cpp
    Decoder.cpp
    ScreenDecoder.cpp
)

target_compile_definitions(ConduitCamera PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN)

target_link_libraries(ConduitCamera PRIVATE
    mfsensorgroup
    mfplat
    mf
    mfuuid
    mfreadwrite
    ole32
    oleaut32
    advapi32
    ksuser
)

# Export DllGetClassObject / DllCanUnloadNow / DllRegister... via the .def file.
set_target_properties(ConduitCamera PROPERTIES
    LINK_FLAGS "/DEF:\"${CMAKE_CURRENT_SOURCE_DIR}/ConduitCamera.def\""
    OUTPUT_NAME "ConduitCamera"
)

# Small manual test harness: registers/creates the virtual camera and holds it open
# so "Conduit Camera" can be verified in the Windows Camera app. Not shipped.
add_executable(ConduitCameraTestHost TestHost.cpp)
target_compile_definitions(ConduitCameraTestHost PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
target_link_libraries(ConduitCameraTestHost PRIVATE mfsensorgroup mfplat mf mfuuid ole32 oleaut32)

# Round-trip check of the shared-memory frame transport (no privileges needed).
add_executable(ConduitCameraSelfTest SelfTest.cpp)
target_compile_definitions(ConduitCameraSelfTest PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN)

# Animated frame feeder for live-path testing before the phone stream exists.
add_executable(ConduitCameraFeeder Feeder.cpp)
target_compile_definitions(ConduitCameraFeeder PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
target_link_libraries(ConduitCameraFeeder PRIVATE advapi32)

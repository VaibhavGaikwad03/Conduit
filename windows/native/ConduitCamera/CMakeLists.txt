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

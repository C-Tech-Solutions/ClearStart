cmake_minimum_required(VERSION 3.24)
project(ClearStartStartup LANGUAGES CXX)
if(NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8 OR NOT CMAKE_GENERATOR_PLATFORM STREQUAL "x64")
  message(FATAL_ERROR "Use Visual Studio with -A x64. The startup guard is native x64 only.")
endif()
include(FetchContent)
FetchContent_Declare(detours
  URL https://codeload.github.com/microsoft/Detours/zip/e4bfd6b03e50de46b47abfbd1e46b384f0c5f833
  URL_HASH SHA256=fef0b17c5c3c9356fb8ba7366a3b196149daadd131fb6a92abeda1cb4d3b9b85)
FetchContent_MakeAvailable(detours)
add_library(detours STATIC
  ${detours_SOURCE_DIR}/src/detours.cpp ${detours_SOURCE_DIR}/src/modules.cpp
  ${detours_SOURCE_DIR}/src/disasm.cpp ${detours_SOURCE_DIR}/src/image.cpp
  ${detours_SOURCE_DIR}/src/creatwth.cpp ${detours_SOURCE_DIR}/src/disolx86.cpp
  ${detours_SOURCE_DIR}/src/disolx64.cpp ${detours_SOURCE_DIR}/src/disolia64.cpp
  ${detours_SOURCE_DIR}/src/disolarm.cpp ${detours_SOURCE_DIR}/src/disolarm64.cpp)
target_include_directories(detours PUBLIC ${detours_SOURCE_DIR}/src)
add_executable(ClearStart.Loader WIN32 Loader.cpp)
add_library(ClearStart.ExplorerGuard SHARED ExplorerGuard.cpp ExplorerGuard.def)
foreach(target detours ClearStart.Loader ClearStart.ExplorerGuard)
  set_property(TARGET ${target} PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
  target_compile_features(${target} PRIVATE cxx_std_17)
  target_compile_definitions(${target} PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00)
  target_compile_options(${target} PRIVATE /W4 /WX /guard:cf)
endforeach()
foreach(target ClearStart.Loader ClearStart.ExplorerGuard)
  target_link_libraries(${target} PRIVATE detours user32 shell32 advapi32 onecore)
  target_link_options(${target} PRIVATE /DYNAMICBASE /NXCOMPAT /guard:cf /WX)
endforeach()
target_link_libraries(ClearStart.Loader PRIVATE ole32)
configure_file(${detours_SOURCE_DIR}/LICENSE.md ${CMAKE_CURRENT_BINARY_DIR}/Detours-LICENSE.txt COPYONLY)

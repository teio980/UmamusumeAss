






function(uma_add_strict_warnings TARGET)

  if(MSVC)
    target_compile_options(${TARGET} PRIVATE
      /W4
      /WX
      /utf-8
      /Zc:__cplusplus
      /Zc:preprocessor
      /permissive-
    )
  endif()


  if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
    target_compile_options(${TARGET} PRIVATE
      -Wall -Wextra -Wpedantic
      -Werror
      -Wcast-align
      -Wconversion
      -Wdouble-promotion
      -Wformat=2
      -Wmissing-declarations
      -Wnon-virtual-dtor
      -Wnull-dereference
      -Wold-style-cast
      -Woverloaded-virtual
      -Wshadow
      -Wsign-conversion
      -Wsuggest-override
      -Wundef
      -Wunused
      -Wuseless-cast
      -Wzero-as-null-pointer-constant
    )


    if(CMAKE_CXX_COMPILER_ID STREQUAL "GNU")
      target_compile_options(${TARGET} PRIVATE
        -Wlogical-op
        -Wduplicated-cond
        -Wduplicated-branches
      )
    endif()
  endif()
endfunction()

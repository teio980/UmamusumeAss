






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
      -Wdouble-promotion
      -Wformat=2
      -Wmissing-declarations
      -Wnon-virtual-dtor
      -Wnull-dereference
      -Wold-style-cast
      -Woverloaded-virtual
      -Wshadow
      -Wundef
      -Wunused
      -Wzero-as-null-pointer-constant
      -Wno-missing-field-initializers
      -Wno-unused-function
      -Wno-unused-but-set-variable
    )

    if(CMAKE_CXX_COMPILER_ID STREQUAL "GNU")
      target_compile_options(${TARGET} PRIVATE
        -Wconversion
        -Wsign-conversion
        -Wsuggest-override
        -Wuseless-cast
        -Wlogical-op
        -Wduplicated-cond
        -Wduplicated-branches
      )
    endif()
  endif()
endfunction()

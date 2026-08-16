import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Button } from './button'

describe('Button', () => {
  it('glass-primary renders the pill radius and press-compression classes', () => {
    render(<Button variant="glass-primary">Save</Button>)

    const button = screen.getByRole('button', { name: 'Save' })
    expect(button).toHaveClass('rounded-full')
    expect(button).toHaveClass('active:scale-[0.965]')
  })

  it('glass-confirm renders the amber archive fill, distinct from destructive', () => {
    render(<Button variant="glass-confirm">Archive it</Button>)

    const button = screen.getByRole('button', { name: 'Archive it' })
    expect(button).toHaveAttribute('data-variant', 'glass-confirm')
    expect(button).toHaveClass('bg-[#E2A542]')
  })
})

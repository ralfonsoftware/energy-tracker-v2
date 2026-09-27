import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { QuietCard } from './quiet-card'

describe('QuietCard', () => {
  it('renders its children', () => {
    render(<QuietCard>Card content</QuietCard>)

    expect(screen.getByText('Card content')).toBeInTheDocument()
  })

  it('applies the surface-quiet fill and rounded.md radius, with no glass-tier depth styling', () => {
    render(<QuietCard data-testid="quiet-card">content</QuietCard>)

    const card = screen.getByTestId('quiet-card')
    expect(card).toHaveClass('bg-surface-quiet')
    expect(card).toHaveClass('border-surface-quiet-border')
    expect(card).toHaveClass('rounded-glass-md')
    expect(card).not.toHaveClass('backdrop-blur-[20px]')
    expect(card).not.toHaveClass('shadow-[0_20px_40px_rgba(40,70,30,0.16)]')
  })

  it('forwards additional className to the card', () => {
    render(
      <QuietCard data-testid="quiet-card" className="custom-class">
        content
      </QuietCard>,
    )

    expect(screen.getByTestId('quiet-card')).toHaveClass('custom-class')
  })
})

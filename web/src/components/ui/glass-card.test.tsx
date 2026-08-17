import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { GlassCard } from './glass-card'

describe('GlassCard', () => {
  it('renders its children', () => {
    render(<GlassCard>Card content</GlassCard>)

    expect(screen.getByText('Card content')).toBeInTheDocument()
  })

  it('applies the surface-glass fill and rounded.md radius', () => {
    render(<GlassCard data-testid="glass-card">content</GlassCard>)

    const card = screen.getByTestId('glass-card')
    expect(card).toHaveClass('bg-surface-glass')
    expect(card).toHaveClass('rounded-glass-md')
  })

  it('forwards additional className to the front card', () => {
    render(
      <GlassCard data-testid="glass-card" className="custom-class">
        content
      </GlassCard>,
    )

    expect(screen.getByTestId('glass-card')).toHaveClass('custom-class')
  })

  it('size="lg" applies the rounded.lg hero radius', () => {
    render(
      <GlassCard data-testid="glass-card" size="lg">
        content
      </GlassCard>,
    )

    expect(screen.getByTestId('glass-card')).toHaveClass('rounded-glass-lg')
    expect(screen.getByTestId('glass-card')).not.toHaveClass('rounded-glass-md')
  })

  it('defaults to the rounded.md drill-down-card radius when size is omitted', () => {
    render(<GlassCard data-testid="glass-card">content</GlassCard>)

    expect(screen.getByTestId('glass-card')).toHaveClass('rounded-glass-md')
    expect(screen.getByTestId('glass-card')).not.toHaveClass('rounded-glass-lg')
  })
})
